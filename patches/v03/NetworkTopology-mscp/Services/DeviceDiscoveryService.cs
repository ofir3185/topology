using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetworkTopology.Models;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;

namespace NetworkTopology.Services
{
    internal static class DeviceDiscoveryService
    {
        public static List<CameraDevice> DiscoverCameraIndex()
        {
            var cameras = new Dictionary<Guid, CameraDevice>();
            Log("=== Camera index discovery start ===");

            TryDiscoverFromItemTree(cameras);
            if (cameras.Count == 0)
                TryDiscoverFromCameraGroups(cameras);

            var result = cameras.Values
                .OrderBy(x => x.CameraName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Log("Camera index ready. Cameras=" + result.Count);
            return result;
        }

        public static List<CameraDevice> EnrichCamerasFromHardware(IEnumerable<CameraDevice> source)
        {
            var cameras = new Dictionary<Guid, CameraDevice>();
            if (source != null)
            {
                foreach (var c in source)
                {
                    if (c == null) continue;
                    cameras[c.CameraId] = Clone(c);
                }
            }

            Log("=== Hardware enrichment start. Existing cameras=" + cameras.Count + " ===");

            try
            {
                var master = EnvironmentManager.Instance.MasterSite;
                if (master == null)
                {
                    Log("Hardware enrichment: MasterSite is null");
                    return cameras.Values.ToList();
                }

                var management = new ManagementServer(master);
                int recorderCount = 0;
                int hardwareCount = 0;
                int cameraCount = 0;

                foreach (var rs in management.RecordingServerFolder.RecordingServers)
                {
                    if (rs == null) continue;

                    try
                    {
                        recorderCount++;
                        foreach (var hw in rs.HardwareFolder.Hardwares)
                        {
                            if (hw == null || !hw.Enabled) continue;
                            hardwareCount++;

                            string host = ExtractHost(hw.Address);

                            foreach (var cam in hw.CameraFolder.Cameras)
                            {
                                if (cam == null || !cam.Enabled) continue;

                                Guid id;
                                if (!Guid.TryParse(cam.Id, out id)) continue;
                                cameraCount++;

                                CameraDevice device;
                                if (!cameras.TryGetValue(id, out device))
                                {
                                    device = new CameraDevice
                                    {
                                        CameraId = id,
                                        State = NodeState.Unknown
                                    };
                                    cameras[id] = device;
                                }

                                device.CameraName = First(device.CameraName, cam.Name);
                                device.HardwareName = First(device.HardwareName, hw.Name);
                                device.Address = First(device.Address, hw.Address);
                                device.IpAddress = First(device.IpAddress, host);
                                device.Model = First(device.Model, hw.Model);
                                device.RecordingServerName = First(device.RecordingServerName, rs.Name);
                                device.HardwarePath = First(device.HardwarePath, hw.Path);
                                device.HardwareUserName = First(device.HardwareUserName, hw.UserName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Recording server '" + SafeName(rs) + "' enrichment failed: " + Flatten(ex));
                    }
                }

                Log("Hardware enrichment detail: recorders=" + recorderCount +
                    ", hardware=" + hardwareCount + ", cameras=" + cameraCount);
            }
            catch (Exception ex)
            {
                Log("Hardware enrichment failed: " + Flatten(ex));
            }

            var result = cameras.Values
                .OrderBy(x => x.CameraName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Log("Hardware enrichment completed. Cameras=" + result.Count);
            return result;
        }

        private static void TryDiscoverFromItemTree(Dictionary<Guid, CameraDevice> cameras)
        {
            List<Item> roots = null;
            try
            {
                roots = Configuration.Instance.GetItemsByKind(Kind.Camera, ItemHierarchy.SystemDefined);
                Log("SystemDefined camera roots=" + (roots == null ? 0 : roots.Count));
            }
            catch (Exception ex)
            {
                Log("GetItemsByKind(SystemDefined) failed: " + Flatten(ex));
            }

            if (roots == null || roots.Count == 0)
            {
                try
                {
                    roots = Configuration.Instance.GetItemsByKind(Kind.Camera);
                    Log("Default camera roots=" + (roots == null ? 0 : roots.Count));
                }
                catch (Exception ex)
                {
                    Log("GetItemsByKind(default) failed: " + Flatten(ex));
                }
            }

            if (roots == null) return;

            var visitedFolders = new HashSet<Guid>();
            var stack = new Stack<KeyValuePair<Item, int>>();
            foreach (var root in roots)
                if (root != null) stack.Push(new KeyValuePair<Item, int>(root, 0));

            int leaves = 0;
            while (stack.Count > 0)
            {
                var pair = stack.Pop();
                var item = pair.Key;
                var depth = pair.Value;
                if (item == null || item.FQID == null || depth > 32) continue;

                if (item.FQID.FolderType == FolderType.No)
                {
                    if (item.FQID.Kind != Kind.Camera) continue;

                    bool enabled = true;
                    try { enabled = item.Enabled; } catch { }
                    if (!enabled) continue;

                    leaves++;
                    var device = new CameraDevice
                    {
                        CameraId = item.FQID.ObjectId,
                        CameraName = item.Name,
                        RecordingServerName = SafeRecorderName(item),
                        State = NodeState.Unknown
                    };

                    TryEnrichFromItemProperties(device, item);
                    cameras[device.CameraId] = device;
                    continue;
                }

                if (!visitedFolders.Add(item.FQID.ObjectId)) continue;

                try
                {
                    var children = item.GetChildren();
                    if (children != null)
                        foreach (var child in children)
                            if (child != null) stack.Push(new KeyValuePair<Item, int>(child, depth + 1));
                }
                catch (Exception ex)
                {
                    Log("GetChildren '" + item.Name + "' failed: " + Flatten(ex));
                }
            }

            Log("Item tree camera leaves=" + leaves);
        }

        private static void TryDiscoverFromCameraGroups(Dictionary<Guid, CameraDevice> cameras)
        {
            try
            {
                var master = EnvironmentManager.Instance.MasterSite;
                if (master == null) return;

                var management = new ManagementServer(master);
                var serverId = master.ServerId;
                int count = 0;

                foreach (var group in management.CameraGroupFolder.CameraGroups)
                    CollectCameraGroup(group, serverId, cameras, ref count, 0);

                Log("CameraGroup fallback cameras=" + count);
            }
            catch (Exception ex)
            {
                Log("CameraGroup fallback failed: " + Flatten(ex));
            }
        }

        private static void CollectCameraGroup(
            CameraGroup group,
            ServerId serverId,
            Dictionary<Guid, CameraDevice> cameras,
            ref int count,
            int depth)
        {
            if (group == null || depth > 32) return;

            try
            {
                foreach (var cam in group.CameraFolder.Cameras)
                {
                    if (cam == null || !cam.Enabled) continue;
                    Guid id;
                    if (!Guid.TryParse(cam.Id, out id)) continue;
                    count++;

                    CameraDevice device;
                    if (!cameras.TryGetValue(id, out device))
                    {
                        device = new CameraDevice
                        {
                            CameraId = id,
                            CameraName = cam.Name,
                            State = NodeState.Unknown
                        };
                        cameras[id] = device;
                    }

                    try
                    {
                        var item = Configuration.Instance.GetItem(serverId, id, Kind.Camera);
                        if (item != null)
                        {
                            device.CameraName = First(device.CameraName, item.Name);
                            device.RecordingServerName = First(device.RecordingServerName, SafeRecorderName(item));
                            TryEnrichFromItemProperties(device, item);
                        }
                    }
                    catch { }
                }

                foreach (var child in group.CameraGroupFolder.CameraGroups)
                    CollectCameraGroup(child, serverId, cameras, ref count, depth + 1);
            }
            catch (Exception ex)
            {
                Log("CameraGroup '" + SafeName(group) + "' failed: " + Flatten(ex));
            }
        }

        private static void TryEnrichFromItemProperties(CameraDevice device, Item item)
        {
            if (device == null || item == null || item.Properties == null) return;
            try
            {
                string value;
                if (item.Properties.TryGetValue(ItemProperties.Address, out value) && !string.IsNullOrWhiteSpace(value))
                {
                    device.Address = value;
                    device.IpAddress = ExtractHost(value);
                }
                else if (item.Properties.TryGetValue(ItemProperties.ConfigurationUrl, out value) && !string.IsNullOrWhiteSpace(value))
                {
                    device.Address = value;
                    device.IpAddress = ExtractHost(value);
                }
            }
            catch { }
        }

        private static CameraDevice Clone(CameraDevice c)
        {
            return new CameraDevice
            {
                CameraId = c.CameraId,
                CameraName = c.CameraName,
                HardwareName = c.HardwareName,
                Address = c.Address,
                IpAddress = c.IpAddress,
                Model = c.Model,
                RecordingServerName = c.RecordingServerName,
                HardwarePath = c.HardwarePath,
                HardwareUserName = c.HardwareUserName,
                State = c.State,
                PingMs = c.PingMs,
                Error = c.Error
            };
        }

        private static string SafeRecorderName(Item item)
        {
            try
            {
                return item.FQID != null && item.FQID.ServerId != null
                    ? item.FQID.ServerId.ServerHostname
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string ExtractHost(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return string.Empty;
            try
            {
                Uri uri;
                if (Uri.TryCreate(address, UriKind.Absolute, out uri)) return uri.Host;
                if (Uri.TryCreate("http://" + address, UriKind.Absolute, out uri)) return uri.Host;
            }
            catch { }
            return address.Trim();
        }

        private static string First(string current, string candidate)
        {
            return string.IsNullOrWhiteSpace(current) ? (candidate ?? string.Empty) : current;
        }

        private static string SafeName(object value)
        {
            try
            {
                var p = value.GetType().GetProperty("Name");
                return p == null ? "?" : Convert.ToString(p.GetValue(value, null));
            }
            catch { return "?"; }
        }

        private static string Flatten(Exception ex)
        {
            if (ex == null) return string.Empty;
            var text = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null)
                text += " -> " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            return text;
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(WorkspaceStoreService.DataDirectory);
                File.AppendAllText(
                    WorkspaceStoreService.DiscoveryLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
