/*
This function returns subtitles from an asset.

Input:
{
    "assetId" : "nb:cid:UUID:88432c30-cb4a-4496-88c2-b2a05ce9033b", // Mandatory, Id of the source asset
    "timeOffset" :"00:01:00", // optional, offset to add to subtitles (used for live analytics)
    "deleteAsset" : true // Optional, delete the asset once data has been read from it
 }

Output:
{
    "vttUrl" : "",      // the full path to vtt file if asset is published
    "ttmlUrl" : "",     // the full path to vtt file if asset is published
    "pathUrl" : "",     // the path to the asset if asset is published
    "vttDocument" : "", // the full vtt document,
    "vttDocumentOffset" : "", // the full vtt document with offset
    "ttmlDocument : ""  // the full ttml document
    "ttmlDocumentOffset : ""  // the full ttml document with offset
 }
*/

#r "Newtonsoft.Json"
#r "Microsoft.WindowsAzure.Storage"
#r "System.Xml.Linq"
#load "../Shared/mediaServicesHelpers.csx"
#load "../Shared/copyBlobHelpers.csx"

using System;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.MediaServices.Client;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Web;
using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.Azure.WebJobs;
using System.Xml.Linq;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

// Read values from the App.config file.
static string _storageAccountName = Environment.GetEnvironmentVariable("MediaServicesStorageAccountName");
static string _storageAccountKey = Environment.GetEnvironmentVariable("MediaServicesStorageAccountKey");

public static string _AADTenantDomain = Environment.GetEnvironmentVariable("AMSAADTenantDomain");
public static string _RESTAPIEndpoint = Environment.GetEnvironmentVariable("AMSRESTAPIEndpoint");

public static string _mediaservicesClientId = Environment.GetEnvironmentVariable("AMSClientId");
public static string _mediaservicesClientSecret = Environment.GetEnvironmentVariable("AMSClientSecret");

// Field for service context.
private static CloudMediaContext _context = null;
private static CloudStorageAccount _destinationStorageAccount = null;

public static string MPPSubtitlesStartTime = "";
public static string MPPSubtitlesEndTime = "";


public static async Task<object> Run(HttpRequestMessage req, TraceWriter log, Microsoft.Azure.WebJobs.ExecutionContext execContext)
{
    log.Info($"Webhook was triggered!");

    // Init variables
    string vttUrl = "";
    string pathUrl = "";
    string ttmlUrl = "";
    string vttContent = "";
    string ttmlContent = "";
    string ttmlContentTimeCorrected = "";
    string vttContentTimeCorrected = "";

    string jsonContent = await req.Content.ReadAsStringAsync();
    dynamic data = JsonConvert.DeserializeObject(jsonContent);

    log.Info(jsonContent);

    if (data.assetId == null)
    {
        // for test
        // data.assetId = "nb:cid:UUID:d9496372-32f5-430d-a4c6-d21ec3e01525";

        return req.CreateResponse(HttpStatusCode.BadRequest, new
        {
            error = "Please pass asset ID in the input object (AssetId)"
        });
    }

    log.Info($"Using Azure Media Service Rest API Endpoint : {_RESTAPIEndpoint}");

    try
    {
        _AADTenantDomain = (string)data.tenantDomain;
        _RESTAPIEndpoint = (string)data.apiUrl;

        _mediaservicesClientId = (string)data.clientId;
        _mediaservicesClientSecret = (string)data.clientSecret;

        if ((string.IsNullOrEmpty(_RESTAPIEndpoint)) || (string.IsNullOrEmpty(_mediaservicesClientId)) 
            || (string.IsNullOrEmpty(_AADTenantDomain)) || (string.IsNullOrEmpty(_mediaservicesClientSecret)))
        {
            log.Info("One or AMS parameters are missing");
            return req.CreateResponse(HttpStatusCode.BadRequest, new
            {
                error = "One or AMS parameters are missing"
            });
        }
        
        AzureAdTokenCredentials tokenCredentials = new AzureAdTokenCredentials(_AADTenantDomain,
                            new AzureAdClientSymmetricKey(_mediaservicesClientId, _mediaservicesClientSecret),
                            AzureEnvironments.AzureCloudEnvironment);

        AzureAdTokenProvider tokenProvider = new AzureAdTokenProvider(tokenCredentials);

        _context = new CloudMediaContext(new Uri(_RESTAPIEndpoint), tokenProvider);

        // Get the asset
        string assetid = data.assetId;
        var outputAsset = _context.Assets.Where(a => a.Id == assetid).FirstOrDefault();

        if (outputAsset == null)
        {
            log.Info($"Asset not found {assetid}");
            return req.CreateResponse(HttpStatusCode.BadRequest, new
            {
                error = "Asset not found"
            });
        }

        var vttSubtitle = outputAsset.AssetFiles.Where(a => a.Name.ToUpper().EndsWith(".VTT")).FirstOrDefault();
        var ttmlSubtitle = outputAsset.AssetFiles.Where(a => a.Name.ToUpper().EndsWith(".TTML")).FirstOrDefault();

        Uri publishurl = GetValidSasPath(outputAsset);
        if (publishurl != null)
        {
            pathUrl = publishurl.ToString();
        }
        else
        {
            log.Info($"Asset not published");
        }

        if (vttSubtitle != null)
        {
            if (publishurl != null)
            {
                vttUrl = pathUrl + vttSubtitle.Name;
            }
            vttContent = ReturnSubContent(vttSubtitle);

            if (data.timeOffset != null) // let's update the ttml with new timecode
            {
                var tsoffset = TimeSpan.Parse((string)data.timeOffset);
                string arrow = " --> ";
                StringBuilder sb = new StringBuilder();
                string[] delim = { Environment.NewLine, "\n" }; // "\n" added in case you manually appended a newline
                string[] vttlines = vttContent.Split(delim, StringSplitOptions.None);

                foreach (string vttline in vttlines)
                {
                    string line = vttline;
                    if (vttline.Contains(arrow))
                    {
                        TimeSpan begin = TimeSpan.Parse(vttline.Substring(0, vttline.IndexOf(arrow)));
                        TimeSpan end = TimeSpan.Parse(vttline.Substring(vttline.IndexOf(arrow) + 5));
                        line = (begin + tsoffset).ToString(@"d\.hh\:mm\:ss\.fff") + arrow + (end + tsoffset).ToString(@"d\.hh\:mm\:ss\.fff");
                    }
                    sb.AppendLine(line);
                }
                vttContentTimeCorrected = sb.ToString();
            }
        }

        if (ttmlSubtitle != null)
        {
            if (publishurl != null)
            {
                ttmlUrl = pathUrl + vttSubtitle.Name;
            }
            ttmlContent = ReturnSubContent(ttmlSubtitle);
            if (data.timeOffset != null) // let's update the vtt with new timecode
            {
                var tsoffset = TimeSpan.Parse((string)data.timeOffset);
                log.Info("tsoffset : " + tsoffset.ToString(@"d\.hh\:mm\:ss\.fff"));
                XNamespace xmlns = "http://www.w3.org/ns/ttml";
                XDocument docXML = XDocument.Parse(ttmlContent);
                var tt = docXML.Element(xmlns + "tt");
                var subtitles = docXML.Element(xmlns + "tt").Element(xmlns + "body").Element(xmlns + "div").Elements(xmlns + "p");
                foreach (var sub in subtitles)
                {
                    var begin = TimeSpan.Parse((string)sub.Attribute("begin"));
                    var end = TimeSpan.Parse((string)sub.Attribute("end"));
                    sub.SetAttributeValue("end", (end + tsoffset).ToString(@"d\.hh\:mm\:ss\.fff"));
                    sub.SetAttributeValue("begin", (begin + tsoffset).ToString(@"d\.hh\:mm\:ss\.fff"));
                }
                ttmlContentTimeCorrected = docXML.Declaration.ToString() + Environment.NewLine + docXML.ToString();
            }
        }

        if (ttmlContent != "" && vttContent != "" && data.deleteAsset != null && ((bool)data.deleteAsset))
        // If asset deletion was asked
        {
            outputAsset.Delete();
        }
    }
    catch (Exception ex)
    {
        log.Info($"Exception {ex}");
        return req.CreateResponse(HttpStatusCode.InternalServerError, new
        {
            Error = ex.ToString()
        });
    }

    log.Info($"");
    return req.CreateResponse(HttpStatusCode.OK, new
    {
        vttUrl = vttUrl,
        ttmlUrl = ttmlUrl,
        pathUrl = pathUrl,
        ttmlDocument = ttmlContent,
        ttmlDocumentWithOffset = ttmlContentTimeCorrected,
        vttDocument = vttContent,
        vttDocumentWithOffset = vttContentTimeCorrected,
        MPPSubtitles = TransformSubtitles(vttContentTimeCorrected),
        MPPSubtitlesStartTime = MPPSubtitlesStartTime,
        MPPSubtitlesEndTime = MPPSubtitlesEndTime
    });
}

//CUSTOM METHOD OF LIVEARENA
//It transforms subtitles into JSON formed text
public static string TransformSubtitles(string text)
{
    string arrow = " --> ";
    var captionText = "[";
    var arr = text.Split(new[] { "\r\n\r\n" }, StringSplitOptions.None);
    for (var i = 1; i < arr.Count() - 1; i++)
    {
        if (!arr[i].Contains(arrow)) continue;
        var captionData = arr[i].Split(new[] { "-->" }, StringSplitOptions.None);
        var temp = captionData[1].Split(new[] { "\r\n" }, StringSplitOptions.None);
        var startTemp = captionData[0].TrimEnd(' ').Split('.');
        var start = startTemp[1];
        var endTemp = temp[0].TrimStart(' ').Split('.');
        var end = endTemp[1];
        captionText += "{\"start\": \"" + start + "\", \"end\": \"" + end + "\", \"text\": \"" + temp[1] + "\"}, ";
        if (i == 1) MPPSubtitlesStartTime = start;
        if (i == arr.Count() - 2) MPPSubtitlesEndTime = end;
    }
    captionText = captionText.TrimEnd(' ');
    captionText = captionText.TrimEnd(',');
    return captionText + "]";
}

public static Uri GetValidSasPath(IAsset asset, string preferredSE = null)
{
    var aivalidurls = GetPaths(asset, preferredSE);
    if (aivalidurls != null)
    {
        return aivalidurls.FirstOrDefault();
    }
    else
    {
        return null;
    }
}

public static IEnumerable<Uri> GetPaths(IAsset asset, string preferredSE = null)
{
    IEnumerable<Uri> ValidURIs;

    var locators = asset.Locators.Where(l => l.Type == LocatorType.Sas && l.ExpirationDateTime > DateTime.UtcNow).OrderByDescending(l => l.ExpirationDateTime);

    if(locators.Count() == 0){
        IAccessPolicy readPolicy = _context.AccessPolicies.Create("readPolicy",
        TimeSpan.FromHours(4), AccessPermissions.Read);
        ILocator outputLocator = _context.Locators.CreateLocator(LocatorType.Sas, asset, readPolicy);
        locators = asset.Locators.Where(l => l.Type == LocatorType.Sas && l.ExpirationDateTime > DateTime.UtcNow).OrderByDescending(l => l.ExpirationDateTime);
    }
    //var se = _context.StreamingEndpoints.AsEnumerable().Where(o => (o.State == StreamingEndpointState.Running) && (CanDoDynPackaging(o))).OrderByDescending(o => o.CdnEnabled);

    var se = _context.StreamingEndpoints.AsEnumerable().Where(o =>
       (string.IsNullOrEmpty(preferredSE) || (o.Name == preferredSE)) &&
       (!string.IsNullOrEmpty(preferredSE) || ((o.State == StreamingEndpointState.Running) &&
       (CanDoDynPackaging(o)))))
       .OrderByDescending(o => o.CdnEnabled);

    if (se.Count() == 0) // No running which can do dynpackaging SE and if not preferredSE. Let's use the default one to get URL
    {
        se = _context.StreamingEndpoints.AsEnumerable().Where(o => o.Name == "default").OrderByDescending(o => o.CdnEnabled);
    }
    
    var template = new UriTemplate("{contentAccessComponent}/");
    ValidURIs = locators.SelectMany(l => se.Select(
                o =>
                    template.BindByPosition(new Uri("https://" + o.HostName), l.ContentAccessComponent)))
        .ToArray();

    return ValidURIs;
}

public static string ReturnSubContent(IAssetFile assetFile)
{
    string datastring = null;

    try
    {
        string tempPath = System.IO.Path.GetTempPath();
        string filePath = Path.Combine(tempPath, assetFile.Name);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        assetFile.Download(filePath);

        StreamReader streamReader = new StreamReader(filePath);
        Encoding fileEncoding = streamReader.CurrentEncoding;
        datastring = streamReader.ReadToEnd();
        streamReader.Close();

        File.Delete(filePath);
    }
    catch
    {

    }
    log.Info($"datastring: {datastring}");
    return datastring;
}
