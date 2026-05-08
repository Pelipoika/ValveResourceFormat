using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Forms;
using GUI.Types.GLViewers;
using GUI.Utils;
using SteamDatabase.ValvePak;
using ValveResourceFormat.Renderer;

namespace GUI.Types.Viewers
{
    class RefragRts(VrfGuiContext vrfGuiContext) : IViewer, IDisposable
    {
        // Parsed data — one or both may be set depending on which file was opened
        private RtServerRequest?  request; // from {uuid}.bin
        private RtServerResponse? response; // from {uuid}-result.bin

        private GLRtsViewer?     glViewer;
        private RendererContext? rendererContext;

        // ── file detection ────────────────────────────────────────────────────

        public static bool IsAccepted(string fileName)
        {
            var name = Path.GetFileName(fileName);

            if (IsResultFileName(name) || IsRequestFileName(name))
                return true;

            // {uuid}.bin  — RT_ServerRequest  (UUID pattern: 8-4-4-4-12 hex digits)
            if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
                IsUuidBasedName(Path.GetFileNameWithoutExtension(name)))
            {
                return true;
            }

            // explicit keyword matches
            return name.Contains("rt_server",      StringComparison.OrdinalIgnoreCase)
                   || name.Contains("refrag_rts",  StringComparison.OrdinalIgnoreCase)
                   || name.Contains("rt_response", StringComparison.OrdinalIgnoreCase);
        }

        // {uuid}-result.bin / {uuid}_result.bin / {uuid}_output.bin  — RT_ServerResponse
        private static bool IsResultFileName(string name) =>
            name.EndsWith("-result.bin", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_result.bin", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_output.bin", StringComparison.OrdinalIgnoreCase);

        // {uuid}_input.bin  — RT_ServerRequest
        private static bool IsRequestFileName(string name) =>
            name.EndsWith("_input.bin", StringComparison.OrdinalIgnoreCase);

        // Matches names that start with a UUID segment (8 hex chars followed by a dash)
        private static bool IsUuidBasedName(string stem) =>
            stem.Length >= 8 &&
            stem[..8].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

        // ── loading ───────────────────────────────────────────────────────────

        public async Task LoadAsync(Stream? stream)
        {
            byte[] data;
            if (stream != null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(false);
                data = ms.ToArray();
            }
            else
            {
                data = await File.ReadAllBytesAsync(vrfGuiContext.FileName!).ConfigureAwait(false);
            }

            var fileName = Path.GetFileName(vrfGuiContext.FileName ?? string.Empty);
            var isResult = IsResultFileName(fileName);

            if (isResult)
            {
                try
                {
                    response = RtServerResponse.Parse(data);
                }
                catch (Exception ex)
                {
                    Log.Error(nameof(RefragRts), $"Failed to parse result file: {ex.Message}");
                }

                var requestPath = GetSiblingRequestPath(vrfGuiContext.FileName!);
                if (requestPath != null && File.Exists(requestPath))
                {
                    try
                    {
                        var reqData = await File.ReadAllBytesAsync(requestPath).ConfigureAwait(false);
                        request = RtServerRequest.Parse(reqData);
                        Log.Info(nameof(RefragRts), $"Auto-loaded sibling request: {Path.GetFileName(requestPath)}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(nameof(RefragRts), $"Failed to parse sibling request: {ex.Message}");
                    }
                }
            }
            else
            {
                try
                {
                    request = RtServerRequest.Parse(data);
                }
                catch (Exception ex)
                {
                    Log.Error(nameof(RefragRts), $"Failed to parse request file: {ex.Message}");
                }

                var resultPath = GetSiblingResultPath(vrfGuiContext.FileName!);
                if (resultPath != null && File.Exists(resultPath))
                {
                    try
                    {
                        var resData = await File.ReadAllBytesAsync(resultPath).ConfigureAwait(false);
                        response = RtServerResponse.Parse(resData);
                        Log.Info(nameof(RefragRts), $"Auto-loaded sibling result: {Path.GetFileName(resultPath)}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(nameof(RefragRts), $"Failed to parse sibling result: {ex.Message}");
                    }
                }
            }

            // Build 3D viewer
            var mapName = GetMapName();
            if (mapName != null)
            {
                var mapContext = CreateMapContext(mapName);
                if (mapContext != null)
                {
                    var spans = BuildSpans();
                    var players = BuildPlayers();
                    var ticks = BuildTicks();
                    var smokes = BuildSmokes();

                    rendererContext = mapContext.CreateRendererContext();
                    glViewer        = new GLRtsViewer(mapContext, rendererContext, mapName, spans, players, ticks, smokes);
                    glViewer.InitializeLoad();
                    rendererContext = null;
                }
            }
        }

        // ── sibling file helpers ──────────────────────────────────────────────

        /// <summary>Given a result file, returns the path of the sibling request file in the same directory.</summary>
        private static string? GetSiblingRequestPath(string resultPath)
        {
            var dir = Path.GetDirectoryName(resultPath) ?? string.Empty;
            var name = Path.GetFileName(resultPath);

            if (name.EndsWith("_output.bin", StringComparison.OrdinalIgnoreCase))
            {
                // {stem}_output.bin  →  {stem}_input.bin
                var stem = name[..^"_output.bin".Length];
                var inputPath = Path.Combine(dir, stem + "_input.bin");
                return File.Exists(inputPath) ? inputPath : null;
            }

            if (name.EndsWith("-result.bin", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(dir, name[..^"-result.bin".Length] + ".bin");

            if (name.EndsWith("_result.bin", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(dir, name[..^"_result.bin".Length] + ".bin");

            return null;
        }

        /// <summary>Given a request file, returns the first existing sibling result/output file, or a default path.</summary>
        private static string? GetSiblingResultPath(string requestPath)
        {
            var dir = Path.GetDirectoryName(requestPath) ?? string.Empty;
            var name = Path.GetFileName(requestPath);

            // {stem}_input.bin  →  {stem}_output.bin
            if (name.EndsWith("_input.bin", StringComparison.OrdinalIgnoreCase))
            {
                var stem = name[..^"_input.bin".Length];
                return FirstExistingPath(dir, stem, "_output.bin", "_result.bin", "-result.bin");
            }

            var baseStem = Path.GetFileNameWithoutExtension(requestPath);
            return FirstExistingPath(dir, baseStem, "-result.bin", "_result.bin", "_output.bin");
        }

        /// <summary>Returns the first path that exists on disk, or the first candidate if none exist.</summary>
        private static string FirstExistingPath(string dir, string stem, params string[] suffixes)
        {
            var paths = suffixes.Select(s => Path.Combine(dir, stem + s)).ToArray();
            return paths.FirstOrDefault(File.Exists) ?? paths[0];
        }

        // ── tab creation ──────────────────────────────────────────────────────

        public void Create(TabPage tabOuterPage)
        {
            var tabControl = new ThemedTabControl { Dock = DockStyle.Fill };
            tabOuterPage.Controls.Add(tabControl);

            if (glViewer != null)
            {
                var viewPage = new ThemedTabPage("3D VIEW");
                viewPage.Controls.Add(glViewer.InitializeUiControls());
                tabControl.Controls.Add(viewPage);
                glViewer.InitializeRenderLoop();
            }

            CreateSummaryTab(tabControl);

            if (response?.RayTraceResponse?.VisibilitySpans.Count > 0)
            {
                CreateVisibilitySpansTab(tabControl);
            }

            tabControl.SelectedIndex = 0;
        }

        // ── map name resolution ───────────────────────────────────────────────

        private string? GetMapName()
        {
            // Best source: map_name from the parsed request
            var mapName = request?.RayTraceRequest?.MapName;
            if (!string.IsNullOrEmpty(mapName))
            {
                return mapName;
            }

            // Fallback: scan task_id / data_url for known map prefixes
            foreach (var candidate in new[] { response?.TaskId ?? string.Empty, response?.DataUrl ?? string.Empty })
            {
                foreach (var seg in candidate.Split(['/', '\\', '?', '&', '=', '-', '_', '.']))
                {
                    if (IsKnownMapName(seg)) return seg;
                }
            }

            // Last resort: prompt
            string? result = null;
            Program.MainForm.Invoke(() =>
                                    {
                                        using var prompt = new PromptForm("Enter map name (e.g. de_dust2)");
                                        if (prompt.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(prompt.ResultText))
                                            result = prompt.ResultText.Trim();
                                    }
                                   );

            return result;
        }

        private static bool IsKnownMapName(string s) =>
            s.StartsWith("de_", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("ar_", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("gg_", StringComparison.OrdinalIgnoreCase);

        // ── VPK context creation ──────────────────────────────────────────────

        private VrfGuiContext? CreateMapContext(string mapName)
        {
            var candidates = new List<string>();

            foreach (var searchPath in Settings.Config.GameSearchPaths)
            {
                if (searchPath.EndsWith("gameinfo.gi", StringComparison.OrdinalIgnoreCase))
                {
                    var mapsDir = Path.Combine(Path.GetDirectoryName(searchPath)!, "maps");
                    candidates.Add(Path.Combine(mapsDir,                           $"{mapName}_dir.vpk"));
                    candidates.Add(Path.Combine(mapsDir,                           $"{mapName}.vpk"));
                }
                else if (searchPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) &&
                         searchPath.Contains(mapName, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(searchPath);
                }
                else if (Directory.Exists(searchPath))
                {
                    candidates.Add(Path.Combine(searchPath, "maps", $"{mapName}_dir.vpk"));
                    candidates.Add(Path.Combine(searchPath, "maps", $"{mapName}.vpk"));
                }
            }

            var defaultCsgo = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo";
            candidates.Add(Path.Combine(defaultCsgo, "maps", $"{mapName}_dir.vpk"));
            candidates.Add(Path.Combine(defaultCsgo, "maps", $"{mapName}.vpk"));

            var vpkPath = candidates.FirstOrDefault(File.Exists);
            if (vpkPath == null)
            {
                Log.Warn(nameof(RefragRts), $"Could not find VPK for map '{mapName}'. Add the CS2 gameinfo.gi to Settings → Game Search Paths.");
                return new VrfGuiContext(vrfGuiContext.FileName, null);
            }

            Log.Info(nameof(RefragRts), $"Loading map '{mapName}' from: {vpkPath}");

            var package = new Package();
            try
            {
                package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
                package.Read(vpkPath);

                var pkgContext = new VrfGuiContext(vpkPath, null) { CurrentPackage = package };
                package = null;
                try
                {
                    var result = new VrfGuiContext($"maps/{mapName}.vmap_c", pkgContext);
                    pkgContext = null;
                    return result;
                }
                finally
                {
                    pkgContext?.Dispose();
                }
            }
            finally
            {
                package?.Dispose();
            }
        }

        // ── data extraction for GLRtsViewer ───────────────────────────────────

        private List<GLRtsViewer.VisSpan> BuildSpans()
        {
            var rawSpans = response?.RayTraceResponse?.VisibilitySpans ?? [];
            return rawSpans
                .Select(s => new GLRtsViewer.VisSpan(s.FirstTick, s.LastTick, s.SourcePlayerSteamId, s.TargetPlayerSteamId))
                .ToList();
        }

        private Dictionary<ulong, GLRtsViewer.PlayerInfo> BuildPlayers()
        {
            var players = request?.RayTraceRequest?.Players ?? [];
            return players.ToDictionary(
                                        p => p.UserSteamId,
                                        p => new GLRtsViewer.PlayerInfo(p.UserSteamId, p.Team)
                                       );
        }

        private Dictionary<int, Dictionary<ulong, GLRtsViewer.PlayerTickState>> BuildTicks()
        {
            var ticks = request?.RayTraceRequest?.Ticks ?? [];
            var result = new Dictionary<int, Dictionary<ulong, GLRtsViewer.PlayerTickState>>(ticks.Count);
            foreach (var tick in ticks)
            {
                var stateMap = new Dictionary<ulong, GLRtsViewer.PlayerTickState>(tick.PlayerStates.Count);
                foreach (var ps in tick.PlayerStates)
                {
                    stateMap[ps.UserSteamId] = new GLRtsViewer.PlayerTickState(
                                                                               new Vector3(ps.Position.X, ps.Position.Y, ps.Position.Z),
                                                                               ps.ViewAngle.HorizontalAngle,
                                                                               ps.ViewAngle.VerticalAngle,
                                                                               ps.DuckAmount
                                                                              );
                }

                result[(int)tick.Id] = stateMap;
            }

            return result;
        }

        private List<GLRtsViewer.RTSSmoke> BuildSmokes()
        {
            const int SmokeDurationTicks = 1280; // 64 tick/s × 20 s

            var ticks = request?.RayTraceRequest?.Ticks ?? [];
            var result = new List<GLRtsViewer.RTSSmoke>();

            foreach (var tick in ticks)
            {
                foreach (var smokeEvent in tick.Smokes)
                {
                    var spawnTick = (int)tick.Id;
                    var destroyTick = spawnTick + SmokeDurationTicks;
                    var pos = new Vector3(smokeEvent.Position.X, smokeEvent.Position.Y, smokeEvent.Position.Z);
                    result.Add(new GLRtsViewer.RTSSmoke(pos, spawnTick, destroyTick));
                }
            }

            return result;
        }

        // ── text tabs ─────────────────────────────────────────────────────────

        private void CreateSummaryTab(ThemedTabControl tabControl)
        {
            var sb = new StringBuilder();

            if (request != null)
            {
                var rtr = request.RayTraceRequest;
                sb.AppendLine("=== RT_ServerRequest ===");
                sb.AppendLine($"Task ID  : {request.TaskId}");
                sb.AppendLine($"Data URL : {request.DataUrl}");
                sb.AppendLine($"Map      : {rtr?.MapName}");
                sb.AppendLine($"Players  : {rtr?.Players.Count ?? 0}");
                sb.AppendLine($"Ticks    : {rtr?.Ticks.Count   ?? 0}");
                if (rtr?.Players.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Players:");
                    foreach (var p in rtr.Players)
                        sb.AppendLine($"  SteamID {p.UserSteamId}  team={p.Team}");
                }
            }

            if (response != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("=== RT_ServerResponse ===");
                sb.AppendLine($"Task ID  : {response.TaskId}");
                sb.AppendLine($"Success  : {response.Success}");
                sb.AppendLine($"Data URL : {response.DataUrl}");
                sb.AppendLine($"Vis Spans: {response.RayTraceResponse?.VisibilitySpans.Count ?? 0}");
            }

            if (sb.Length == 0)
                sb.AppendLine("No data parsed.");

            var page = new ThemedTabPage("SUMMARY");
            page.Controls.Add(CodeTextBox.Create(sb.ToString(), CodeTextBox.HighlightLanguage.None));
            tabControl.Controls.Add(page);
        }

        private void CreateVisibilitySpansTab(ThemedTabControl tabControl)
        {
            var spans = response?.RayTraceResponse?.VisibilitySpans ?? [];
            var sb = new StringBuilder();
            sb.AppendLine($"{"#",-6} {"FirstTick",-12} {"LastTick",-12} {"Duration",-10} {"Source SteamID",-22} {"Target SteamID",-22}");
            sb.AppendLine(new string('-', 88));
            var i = 0;
            foreach (var span in spans)
            {
                sb.AppendLine(
                              $"{i,-6} {span.FirstTick,-12} {span.LastTick,-12} {span.LastTick - span.FirstTick,-10} {span.SourcePlayerSteamId,-22} {span.TargetPlayerSteamId,-22}"
                             );

                i++;
            }

            var page = new ThemedTabPage("VISIBILITY SPANS");
            page.Controls.Add(CodeTextBox.Create(sb.ToString(), CodeTextBox.HighlightLanguage.None));
            tabControl.Controls.Add(page);
        }

        public void Dispose()
        {
            glViewer?.Dispose();
            rendererContext?.Dispose();
        }

        // =====================================================================
        // Proto3 parsers
        // =====================================================================

        // ── RT_ServerRequest ──────────────────────────────────────────────────

        private sealed class RtServerRequest
        {
            public string             TaskId          { get; private set; } = string.Empty;
            public string             DataUrl         { get; private set; } = string.Empty;
            public RtRayTraceRequest? RayTraceRequest { get; private set; }

            public static RtServerRequest Parse(byte[] data)
            {
                var msg = new RtServerRequest();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: msg.TaskId          = r.ReadString(); break; // task_id
                        case 2: msg.RayTraceRequest = RtRayTraceRequest.Parse(r.ReadBytes()); break; // ray_trace_request
                        case 3: msg.DataUrl         = r.ReadString(); break; // data_url
                        default: r.Skip(wt); break;
                    }
                }

                return msg;
            }
        }

        private sealed class RtRayTraceRequest
        {
            public List<RtPlayer>          Players          { get; }              = [];
            public List<RtTick>            Ticks            { get; }              = [];
            public string                  MapName          { get; private set; } = string.Empty;
            public List<RtEventDescriptor> EventDescriptors { get; }              = [];
            public List<RtDoorEvent>       DoorEvents       { get; }              = [];

            public static RtRayTraceRequest Parse(byte[] data)
            {
                var msg = new RtRayTraceRequest();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: msg.Players.Add(RtPlayer.Parse(r.ReadBytes())); break; // players
                        case 2: msg.Ticks.Add(RtTick.Parse(r.ReadBytes())); break; // ticks
                        case 3: msg.MapName = r.ReadString(); break; // map_name
                        case 4: msg.EventDescriptors.Add(RtEventDescriptor.Parse(r.ReadBytes())); break; // event_descriptors
                        case 5: msg.DoorEvents.Add(RtDoorEvent.Parse(r.ReadBytes())); break; // door_events
                        default: r.Skip(wt); break;
                    }
                }

                return msg;
            }
        }

        private sealed class RtPlayer
        {
            public ulong UserSteamId { get; private set; }
            public uint  Team        { get; private set; }

            public static RtPlayer Parse(byte[] data)
            {
                var p = new RtPlayer();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: p.UserSteamId = r.ReadVarint(); break; // user_steam_id
                        case 2: p.Team        = (uint)r.ReadVarint(); break; // team
                        default: r.Skip(wt); break;
                    }
                }

                return p;
            }
        }

        private sealed class RtTick
        {
            public uint                Id           { get; private set; }
            public List<RtPlayerState> PlayerStates { get; } = [];
            public List<RtSmokeEvent>  Smokes       { get; } = [];
            public List<RtEvent>       Events       { get; } = [];

            public static RtTick Parse(byte[] data)
            {
                var t = new RtTick();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: t.Id = (uint)r.ReadVarint(); break; // id
                        case 2: t.PlayerStates.Add(RtPlayerState.Parse(r.ReadBytes())); break; // player_states
                        case 3: t.Smokes.Add(RtSmokeEvent.Parse(r.ReadBytes())); break; // smokes
                        case 4: t.Events.Add(RtEvent.Parse(r.ReadBytes())); break; // events
                        default: r.Skip(wt); break;
                    }
                }

                return t;
            }
        }

        private sealed class RtEvent
        {
            public uint Id { get; private set; }

            public static RtEvent Parse(byte[] data)
            {
                var e = new RtEvent();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: e.Id = (uint)r.ReadVarint(); break; // id
                        default: r.Skip(wt); break;
                    }
                }

                return e;
            }
        }

        private sealed class RtEventDescriptor
        {
            public uint   Id   { get; private set; }
            public string Name { get; private set; } = string.Empty;

            public static RtEventDescriptor Parse(byte[] data)
            {
                var d = new RtEventDescriptor();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: d.Id   = (uint)r.ReadVarint(); break; // id
                        case 2: d.Name = r.ReadString(); break; // name
                        default: r.Skip(wt); break;
                    }
                }

                return d;
            }
        }

        private sealed class RtDoorEvent
        {
            public ulong  Id         { get; private set; }
            public int    Tick       { get; private set; }
            public string EntityType { get; private set; } = string.Empty;
            public float  X          { get; private set; }
            public float  Y          { get; private set; }
            public float  Z          { get; private set; }
            public float  AngX       { get; private set; }
            public float  AngY       { get; private set; }
            public string EventName  { get; private set; } = string.Empty;

            public static RtDoorEvent Parse(byte[] data)
            {
                var d = new RtDoorEvent();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: d.Id         = r.ReadVarint(); break; // id (uint64)
                        case 2: d.Tick       = (int)r.ReadVarint(); break; // tick
                        case 3: d.EntityType = r.ReadString(); break; // entity_type
                        case 4: d.X          = r.ReadFloat(); break; // x
                        case 5: d.Y          = r.ReadFloat(); break; // y
                        case 6: d.Z          = r.ReadFloat(); break; // z
                        case 7: d.AngX       = r.ReadFloat(); break; // ang_x
                        case 8: d.AngY       = r.ReadFloat(); break; // ang_y
                        case 9: d.EventName  = r.ReadString(); break; // event_name
                        default: r.Skip(wt); break;
                    }
                }

                return d;
            }
        }

        private sealed class RtSmokeEvent
        {
            public RtPosition Position { get; private set; } = new();

            public static RtSmokeEvent Parse(byte[] data)
            {
                var s = new RtSmokeEvent();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: s.Position = RtPosition.Parse(r.ReadBytes()); break; // position
                        default: r.Skip(wt); break;
                    }
                }

                return s;
            }
        }

        private sealed class RtPlayerState
        {
            public ulong       UserSteamId { get; private set; }
            public RtPosition  Position    { get; private set; } = new();
            public RtViewAngle ViewAngle   { get; private set; } = new();
            public uint        DuckAmount  { get; private set; }

            public static RtPlayerState Parse(byte[] data)
            {
                var ps = new RtPlayerState();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: ps.UserSteamId = r.ReadVarint(); break; // user_steam_id
                        case 2: ps.Position    = RtPosition.Parse(r.ReadBytes()); break; // position
                        case 3: ps.ViewAngle   = RtViewAngle.Parse(r.ReadBytes()); break; // view_angle
                        case 4: ps.DuckAmount  = (uint)r.ReadVarint(); break; // duck_amount
                        default: r.Skip(wt); break;
                    }
                }

                return ps;
            }
        }

        private sealed class RtPosition
        {
            public int X { get; private set; }
            public int Y { get; private set; }
            public int Z { get; private set; }

            public static RtPosition Parse(byte[] data)
            {
                var p = new RtPosition();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        // int32 stored as varint — cast ulong back to int to handle negative values
                        case 1: p.X = (int)r.ReadVarint(); break;
                        case 2: p.Y = (int)r.ReadVarint(); break;
                        case 3: p.Z = (int)r.ReadVarint(); break;
                        default: r.Skip(wt); break;
                    }
                }

                return p;
            }
        }

        private sealed class RtViewAngle
        {
            public int HorizontalAngle { get; private set; }
            public int VerticalAngle   { get; private set; }

            public static RtViewAngle Parse(byte[] data)
            {
                var v = new RtViewAngle();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: v.HorizontalAngle = (int)r.ReadVarint(); break;
                        case 2: v.VerticalAngle   = (int)r.ReadVarint(); break;
                        default: r.Skip(wt); break;
                    }
                }

                return v;
            }
        }

        // ── RT_ServerResponse ─────────────────────────────────────────────────

        private sealed class RtServerResponse
        {
            public string              TaskId           { get; private set; } = string.Empty;
            public bool                Success          { get; private set; }
            public string              DataUrl          { get; private set; } = string.Empty;
            public RtRayTraceResponse? RayTraceResponse { get; private set; }

            public static RtServerResponse Parse(byte[] data)
            {
                var msg = new RtServerResponse();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: msg.TaskId           = r.ReadString(); break;
                        case 2: msg.Success          = r.ReadVarint() != 0; break;
                        case 3: msg.RayTraceResponse = RtRayTraceResponse.Parse(r.ReadBytes()); break;
                        case 4: msg.DataUrl          = r.ReadString(); break;
                        default: r.Skip(wt); break;
                    }
                }

                return msg;
            }
        }

        private sealed class RtRayTraceResponse
        {
            public List<RtVisibilitySpan> VisibilitySpans { get; } = [];

            public static RtRayTraceResponse Parse(byte[] data)
            {
                var msg = new RtRayTraceResponse();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: msg.VisibilitySpans.Add(RtVisibilitySpan.Parse(r.ReadBytes())); break;
                        default: r.Skip(wt); break;
                    }
                }

                return msg;
            }
        }

        private sealed class RtVisibilitySpan
        {
            public int   FirstTick           { get; private set; }
            public int   LastTick            { get; private set; }
            public ulong SourcePlayerSteamId { get; private set; }
            public ulong TargetPlayerSteamId { get; private set; }

            public static RtVisibilitySpan Parse(byte[] data)
            {
                var s = new RtVisibilitySpan();
                var r = new ProtoReader(data);
                while (r.TryReadTag(out var fn, out var wt))
                {
                    switch (fn)
                    {
                        case 1: s.FirstTick           = (int)r.ReadVarint(); break;
                        case 2: s.LastTick            = (int)r.ReadVarint(); break;
                        case 3: s.SourcePlayerSteamId = r.ReadVarint(); break;
                        case 4: s.TargetPlayerSteamId = r.ReadVarint(); break;
                        default: r.Skip(wt); break;
                    }
                }

                return s;
            }
        }

        // ── Shared proto3 reader ──────────────────────────────────────────────

        private ref struct ProtoReader(ReadOnlySpan<byte> data)
        {
            private ReadOnlySpan<byte> _data = data;
            private int                _pos  = 0;

            public bool TryReadTag(out int fieldNumber, out int wireType)
            {
                // Skip zero padding bytes
                while (_pos < _data.Length && _data[_pos] == 0)
                    _pos++;

                if (_pos >= _data.Length)
                {
                    fieldNumber = 0;
                    wireType    = 0;
                    return false;
                }

                var tag = ReadVarint();
                fieldNumber = (int)(tag >> 3);
                wireType    = (int)(tag & 0x7);
                if (fieldNumber <= 0)
                {
                    // Unrecoverable — consume rest of buffer
                    _pos = _data.Length;
                    return false;
                }

                return true;
            }

            public ulong ReadVarint()
            {
                ulong result = 0;
                var shift = 0;
                while (_pos < _data.Length)
                {
                    var b = _data[_pos++];
                    result |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return result;

                    shift += 7;
                    if (shift >= 64) break;
                }

                return result;
            }

            public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

            public float ReadFloat()
            {
                if (_pos + 4 > _data.Length)
                {
                    _pos = _data.Length;
                    return 0f;
                }

                var value = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_pos, 4));
                _pos += 4;
                return value;
            }

            public byte[] ReadBytes()
            {
                var len = (int)ReadVarint();
                if (len <= 0 || _pos + len > _data.Length)
                {
                    // Clamp to available bytes rather than throwing
                    len = Math.Max(0, Math.Min(len, _data.Length - _pos));
                }

                var bytes = _data.Slice(_pos, len).ToArray();
                _pos += len;
                return bytes;
            }

            public void Skip(int wireType)
            {
                switch (wireType)
                {
                    case 0: ReadVarint(); break;
                    case 1: _pos = Math.Min(_pos + 8, _data.Length); break;
                    case 2:
                        var len = (int)ReadVarint();
                        _pos = Math.Min(_pos + Math.Max(0, len), _data.Length);
                        break;
                    case 5: _pos = Math.Min(_pos + 4, _data.Length); break;
                    default:
                        // Unknown wire type — consume rest of buffer to avoid infinite loop
                        _pos = _data.Length;
                        break;
                }
            }
        }
    }
}
