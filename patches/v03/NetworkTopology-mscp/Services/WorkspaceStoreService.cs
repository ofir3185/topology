using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NetworkTopology.Models;

namespace NetworkTopology.Services
{
    internal static class WorkspaceStoreService
    {
        public static string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Megalcom", "NetworkTopology");

        public static string WorkspacePath => Path.Combine(DataDirectory, "boards.xml");
        public static string DiscoveryLogPath => Path.Combine(DataDirectory, "discovery.log");

        public static WorkspaceState Load()
        {
            try
            {
                if (!File.Exists(WorkspacePath))
                    return CreateDefault();

                var doc = XDocument.Load(WorkspacePath);
                var root = doc.Root;
                if (root == null)
                    return CreateDefault();

                var state = new WorkspaceState
                {
                    ActiveBoardId = Attr(root, "activeBoard")
                };

                var switchRoot = root.Element("SwitchLibrary");
                if (switchRoot != null)
                {
                    foreach (var e in switchRoot.Elements("Switch"))
                    {
                        state.Switches.Add(new SwitchNode
                        {
                            Id = Attr(e, "id"),
                            Name = Attr(e, "name"),
                            IpAddress = Attr(e, "ip"),
                            Role = Attr(e, "role"),
                            ChassisId = Attr(e, "chassis"),
                            Description = Attr(e, "description"),
                            Source = Attr(e, "source"),
                            AutoDiscovered = BoolAttr(e, "auto"),
                            State = NodeState.Unknown
                        });
                    }
                }

                var boardsRoot = root.Element("Boards");
                if (boardsRoot != null)
                {
                    foreach (var b in boardsRoot.Elements("Board"))
                    {
                        var board = new TopologyBoard
                        {
                            Id = Attr(b, "id"),
                            Name = Attr(b, "name")
                        };

                        foreach (var n in b.Elements("Node"))
                        {
                            double x, y;
                            double.TryParse(Attr(n, "x"), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out x);
                            double.TryParse(Attr(n, "y"), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out y);

                            board.Nodes.Add(new BoardNode
                            {
                                Id = Attr(n, "id"),
                                Kind = Attr(n, "kind"),
                                DeviceKey = Attr(n, "deviceKey"),
                                DisplayName = Attr(n, "name"),
                                IpAddress = Attr(n, "ip"),
                                X = x,
                                Y = y
                            });
                        }

                        foreach (var l in b.Elements("Link"))
                        {
                            board.Links.Add(new BoardLink
                            {
                                Id = Attr(l, "id"),
                                FromNodeId = Attr(l, "from"),
                                ToNodeId = Attr(l, "to"),
                                Label = Attr(l, "label")
                            });
                        }

                        if (string.IsNullOrWhiteSpace(board.Id))
                            board.Id = Guid.NewGuid().ToString("N");
                        if (string.IsNullOrWhiteSpace(board.Name))
                            board.Name = "Board";

                        state.Boards.Add(board);
                    }
                }

                EnsureValid(state);
                return state;
            }
            catch
            {
                return CreateDefault();
            }
        }

        public static void Save(WorkspaceState state)
        {
            if (state == null) return;

            EnsureValid(state);
            Directory.CreateDirectory(DataDirectory);

            var root = new XElement("NetworkTopologyWorkspace",
                new XAttribute("activeBoard", state.ActiveBoardId ?? string.Empty));

            var switches = new XElement("SwitchLibrary");
            foreach (var sw in state.Switches.OrderBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase))
            {
                switches.Add(new XElement("Switch",
                    new XAttribute("id", sw.Id ?? string.Empty),
                    new XAttribute("name", sw.Name ?? string.Empty),
                    new XAttribute("ip", sw.IpAddress ?? string.Empty),
                    new XAttribute("role", sw.Role ?? string.Empty),
                    new XAttribute("chassis", sw.ChassisId ?? string.Empty),
                    new XAttribute("description", sw.Description ?? string.Empty),
                    new XAttribute("source", sw.Source ?? string.Empty),
                    new XAttribute("auto", sw.AutoDiscovered ? "true" : "false")));
            }
            root.Add(switches);

            var boards = new XElement("Boards");
            foreach (var board in state.Boards)
            {
                var b = new XElement("Board",
                    new XAttribute("id", board.Id ?? string.Empty),
                    new XAttribute("name", board.Name ?? string.Empty));

                foreach (var node in board.Nodes)
                {
                    b.Add(new XElement("Node",
                        new XAttribute("id", node.Id ?? string.Empty),
                        new XAttribute("kind", node.Kind ?? string.Empty),
                        new XAttribute("deviceKey", node.DeviceKey ?? string.Empty),
                        new XAttribute("name", node.DisplayName ?? string.Empty),
                        new XAttribute("ip", node.IpAddress ?? string.Empty),
                        new XAttribute("x", node.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("y", node.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))));
                }

                foreach (var link in board.Links)
                {
                    b.Add(new XElement("Link",
                        new XAttribute("id", link.Id ?? string.Empty),
                        new XAttribute("from", link.FromNodeId ?? string.Empty),
                        new XAttribute("to", link.ToNodeId ?? string.Empty),
                        new XAttribute("label", link.Label ?? string.Empty)));
                }

                boards.Add(b);
            }
            root.Add(boards);

            var temp = WorkspacePath + ".tmp";
            new XDocument(root).Save(temp);
            if (File.Exists(WorkspacePath)) File.Delete(WorkspacePath);
            File.Move(temp, WorkspacePath);
        }

        public static void ImportLegacySwitches(WorkspaceState state, TopologyConfiguration legacy)
        {
            if (state == null || legacy == null) return;

            foreach (var old in legacy.Switches)
            {
                if (old == null) continue;
                var existing = state.Switches.FirstOrDefault(x =>
                    (!string.IsNullOrWhiteSpace(old.Id) && string.Equals(x.Id, old.Id, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(old.IpAddress) && string.Equals(x.IpAddress, old.IpAddress, StringComparison.OrdinalIgnoreCase)));

                if (existing != null) continue;

                state.Switches.Add(new SwitchNode
                {
                    Id = string.IsNullOrWhiteSpace(old.Id) ? "sw-" + Guid.NewGuid().ToString("N") : old.Id,
                    Name = string.IsNullOrWhiteSpace(old.Name) ? "Switch" : old.Name,
                    IpAddress = old.IpAddress,
                    Role = old.Role,
                    Source = "topology.xml",
                    AutoDiscovered = false,
                    State = NodeState.Unknown
                });
            }
        }

        private static WorkspaceState CreateDefault()
        {
            var board = new TopologyBoard
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Main"
            };

            return new WorkspaceState
            {
                ActiveBoardId = board.Id,
                Boards = new List<TopologyBoard> { board },
                Switches = new List<SwitchNode>()
            };
        }

        private static void EnsureValid(WorkspaceState state)
        {
            if (state.Boards == null) state.Boards = new List<TopologyBoard>();
            if (state.Switches == null) state.Switches = new List<SwitchNode>();

            if (state.Boards.Count == 0)
            {
                var board = new TopologyBoard
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Main"
                };
                state.Boards.Add(board);
                state.ActiveBoardId = board.Id;
            }

            if (string.IsNullOrWhiteSpace(state.ActiveBoardId) ||
                !state.Boards.Any(x => string.Equals(x.Id, state.ActiveBoardId, StringComparison.OrdinalIgnoreCase)))
            {
                state.ActiveBoardId = state.Boards[0].Id;
            }

            foreach (var board in state.Boards)
            {
                if (board.Nodes == null) board.Nodes = new List<BoardNode>();
                if (board.Links == null) board.Links = new List<BoardLink>();
            }
        }

        private static string Attr(XElement e, string name)
        {
            return (string)e.Attribute(name) ?? string.Empty;
        }

        private static bool BoolAttr(XElement e, string name)
        {
            bool value;
            return bool.TryParse(Attr(e, name), out value) && value;
        }
    }
}
