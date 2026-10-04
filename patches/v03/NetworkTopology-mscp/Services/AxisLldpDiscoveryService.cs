using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NetworkTopology.Models;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;

namespace NetworkTopology.Services
{
    internal static class AxisLldpDiscoveryService
    {
        private sealed class CameraCredentials
        {
            public string UserName;
            public string Password;
        }

        public static async Task<SwitchDiscoveryResult> TryDiscoverAsync(CameraDevice camera)
        {
            if (camera == null)
                return Fail("Camera is null");

            if (string.IsNullOrWhiteSpace(camera.IpAddress) && string.IsNullOrWhiteSpace(camera.Address))
                return Fail("Camera address is not available yet");

            if (string.IsNullOrWhiteSpace(camera.HardwarePath))
                return Fail("XProtect hardware metadata is not loaded yet");

            CameraCredentials credentials;
            try
            {
                credentials = await Task.Run(() => ReadCredentials(camera)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail("Could not read the camera credentials from XProtect: " + ex.Message);
            }

            if (credentials == null || string.IsNullOrWhiteSpace(credentials.UserName))
                return Fail("Camera credentials are unavailable in XProtect");

            var baseUris = BuildCandidateBaseUris(camera);
            if (baseUris.Count == 0)
                return Fail("No camera HTTP endpoint could be built");

            string lastError = null;
            foreach (var baseUri in baseUris)
            {
                try
                {
                    var result = await QueryNeighborsAsync(baseUri, credentials).ConfigureAwait(false);
                    if (result.Success)
                        return result;
                    lastError = result.Error;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return Fail(string.IsNullOrWhiteSpace(lastError)
                ? "LLDP neighbor information is not available on this camera"
                : lastError);
        }

        private static CameraCredentials ReadCredentials(CameraDevice camera)
        {
            var master = EnvironmentManager.Instance.MasterSite;
            if (master == null || master.ServerId == null)
                return null;

            var hw = new Hardware(master.ServerId, camera.HardwarePath);
            var userName = string.IsNullOrWhiteSpace(camera.HardwareUserName) ? hw.UserName : camera.HardwareUserName;
            var task = hw.ReadPasswordHardware();
            var password = task == null ? null : task.GetProperty("Password");

            return new CameraCredentials
            {
                UserName = userName,
                Password = password ?? string.Empty
            };
        }

        private static List<Uri> BuildCandidateBaseUris(CameraDevice camera)
        {
            var list = new List<Uri>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Uri original;
            if (!string.IsNullOrWhiteSpace(camera.Address) &&
                Uri.TryCreate(camera.Address, UriKind.Absolute, out original))
            {
                AddBase(list, seen, new Uri(original.GetLeftPart(UriPartial.Authority) + "/"));
                if (!string.Equals(original.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    AddBase(list, seen, new Uri("https://" + original.Host + "/"));
            }

            if (!string.IsNullOrWhiteSpace(camera.IpAddress))
            {
                Uri https;
                if (Uri.TryCreate("https://" + camera.IpAddress + "/", UriKind.Absolute, out https))
                    AddBase(list, seen, https);

                Uri http;
                if (Uri.TryCreate("http://" + camera.IpAddress + "/", UriKind.Absolute, out http))
                    AddBase(list, seen, http);
            }

            return list.OrderByDescending(x =>
                string.Equals(x.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static void AddBase(List<Uri> list, HashSet<string> seen, Uri uri)
        {
            if (uri == null) return;
            var key = uri.AbsoluteUri.TrimEnd('/');
            if (seen.Add(key)) list.Add(uri);
        }

        private static async Task<SwitchDiscoveryResult> QueryNeighborsAsync(Uri baseUri, CameraCredentials credentials)
        {
            using (var handler = new HttpClientHandler())
            {
                handler.Credentials = new NetworkCredential(credentials.UserName, credentials.Password);
                handler.PreAuthenticate = true;
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(3.5);
                    var endpoint = new Uri(baseUri, "config/rest/lldp/v1/neighbors");
                    using (var response = await client.GetAsync(endpoint).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return Fail("LLDP API returned HTTP " + (int)response.StatusCode + " from " + baseUri.Host);

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ParseNeighborResponse(json);
                    }
                }
            }
        }

        private static SwitchDiscoveryResult ParseNeighborResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Fail("LLDP API returned an empty response");

            try
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return Fail("Unexpected LLDP response");

                object dataObject;
                if (!root.TryGetValue("data", out dataObject) || dataObject == null)
                    return Fail("LLDP response did not contain neighbor data");

                var neighbors = ToObjectList(dataObject)
                    .Select(x => x as Dictionary<string, object>)
                    .Where(x => x != null)
                    .Select(ParseNeighbor)
                    .Where(x => x != null)
                    .ToList();

                if (neighbors.Count == 0)
                    return Fail("The camera reports no LLDP/CDP neighbors");

                var selected = neighbors
                    .OrderByDescending(ScoreNeighbor)
                    .First();

                selected.Success = true;
                return selected;
            }
            catch (Exception ex)
            {
                return Fail("Could not parse LLDP data: " + ex.Message);
            }
        }

        private static SwitchDiscoveryResult ParseNeighbor(Dictionary<string, object> n)
        {
            var mgmt = Dict(n, "mgmtIP");
            var port = Dict(n, "portID");
            var chassis = Dict(n, "chassisID");

            var sysName = Text(n, "sysName");
            var description = Text(n, "sysDescr");
            var name = !string.IsNullOrWhiteSpace(sysName)
                ? sysName
                : ShortDescription(description);

            if (string.IsNullOrWhiteSpace(name))
                name = "LLDP neighbor";

            return new SwitchDiscoveryResult
            {
                SwitchName = name,
                ManagementIp = Text(mgmt, "value"),
                PortId = Text(port, "value"),
                ChassisId = Text(chassis, "value"),
                Description = description,
                Protocol = Text(n, "protocol")
            };
        }

        private static int ScoreNeighbor(SwitchDiscoveryResult n)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(n.ManagementIp)) score += 20;
            if (!string.IsNullOrWhiteSpace(n.PortId)) score += 10;
            if (!string.IsNullOrWhiteSpace(n.SwitchName)) score += 5;

            var text = ((n.SwitchName ?? string.Empty) + " " + (n.Description ?? string.Empty)).ToLowerInvariant();
            string[] switchWords = { "switch", "cisco", "aruba", "procurve", "hpe", "hewlett", "mikrotik", "netgear", "juniper", "extreme", "fortiswitch", "d-link", "allied" };
            if (switchWords.Any(text.Contains)) score += 50;
            return score;
        }

        private static List<object> ToObjectList(object value)
        {
            var array = value as object[];
            if (array != null) return array.ToList();

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                var list = new List<object>();
                foreach (var item in enumerable) list.Add(item);
                return list;
            }

            return new List<object>();
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string Text(Dictionary<string, object> source, string key)
        {
            if (source == null) return string.Empty;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return string.Empty;
            return Convert.ToString(value) ?? string.Empty;
        }

        private static string ShortDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            return value.Length <= 70 ? value : value.Substring(0, 67) + "...";
        }

        private static SwitchDiscoveryResult Fail(string error)
        {
            return new SwitchDiscoveryResult
            {
                Success = false,
                Error = error ?? "Unknown error"
            };
        }
    }
}
