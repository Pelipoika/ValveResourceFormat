using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Utils;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;
using static ValveResourceFormat.Renderer.PickingTexture;

namespace GUI.Types.GLViewers
{
    internal class GLRtsViewer : GLSceneViewer
    {
        // ── Public data types ────────────────────────────────────────────────

        public sealed record VisSpan(int FirstTick, int LastTick, ulong SourceId, ulong TargetId);

        public sealed record PlayerInfo(ulong SteamId, uint Team);

        /// <summary>An active smoke grenade instance. Active from SpawnTick until DestroyTick (exclusive).</summary>
        public sealed record RTSSmoke(Vector3 Position, int SpawnTick, int DestroyTick);

        /// <summary>A door or breakable state-change event from the RTS proto.</summary>
        public sealed record RTSDoorEvent(int Tick, float X, float Y, float Z, float AngX, float AngY, string EventName);

        /// <summary>Per-player state at a single tick.</summary>
        public sealed record PlayerTickState(
            Vector3 FeetPosition,
            int     HorizontalAngle, // degrees × 1 (raw int32 from proto)
            int     VerticalAngle,
            uint    DuckAmount);

        // ── Inputs ───────────────────────────────────────────────────────────

        private readonly IReadOnlyList<VisSpan>                 spans;
        private readonly IReadOnlyDictionary<ulong, PlayerInfo> players;

        // tickId → (steamId → state)
        private readonly IReadOnlyDictionary<int, Dictionary<ulong, PlayerTickState>> ticks;

        // tickId → list of smokes active at that tick (pre-filtered at build time)
        private readonly Dictionary<int, IReadOnlyList<RTSSmoke>> smokesByTick;

        // Door/breakable events sorted by tick (fed from proto door_events)
        private readonly IReadOnlyList<RTSDoorEvent> _doorEvents;

        private readonly string             mapName;
        private readonly int                tickMin;
        private readonly int                tickMax;
        private readonly IReadOnlyList<int> sortedTickIds;

        // ── Runtime state ────────────────────────────────────────────────────

        private          int  currentTick;
        private          bool showVisLines = true;
        private          bool showPlayers  = true;
        private          bool showViewRays;
        private          bool showSmokes = true;
        private          bool _showDoors = true;
        private volatile bool overlayDirty;

        private readonly List<SceneNode> overlayNodes = [];

        // Loaded door/breakable entities — permanent scene nodes repositioned per tick
        private readonly List<DoorEntity> _doorEntities = [];

        private Label?                 tickLabel;
        private Label?                 spanCountLabel;
        private GLViewerSliderControl? tickScrubber;
        private ulong                  teleportTargetId;

        // Maps each player capsule node → its SteamId so OnPicked can identify it
        private readonly Dictionary<SceneNode, ulong> capsuleSteamIds = [];

        // Ray lines drawn when a player capsule is clicked
        private readonly List<SceneNode> clickRayNodes = [];

        // World-space billboard labels for the click-ray FOV annotations
        private readonly List<(Vector3 WorldPos, string Label, Color32 Color)> clickRayLabels = [];

        // World-space billboard labels showing SteamId above each player capsule
        private readonly List<(Vector3 WorldPos, string Label, Color32 Color)> playerSteamIdLabels = [];

        // ── Source-engine constants ───────────────────────────────────────────

        // Eye height offsets in Source units (ref: addendum spec)
        private const float EyeHeightStanding    = 64f;
        private const float EyeHeightCrouchDelta = 18f; // full-crouch subtracts this
        private const float PlayerRadius         = 16f;
        private const float PlayerHalfHeight     = 36f;
        private const float ViewRayLength        = 200f; // units

        // Team colours
        private static readonly Color32 ColorT       = new(255, 150, 0, 200); // orange
        private static readonly Color32 ColorCT      = new(50, 150, 255, 200); // blue
        private static readonly Color32 ColorUnknown = new(180, 180, 180, 200);
        private static readonly Color32 VisLineColor = new(255, 255, 0, 240);
        private static readonly Color32 ViewRayColor = new(255, 255, 255, 180);
        private static readonly Color32 SmokeColor   = new(160, 200, 160, 60); // translucent green-grey

        private const float SmokeRadius = 150f; // world units (matches server-side constant)

        // ── Constructor ──────────────────────────────────────────────────────

        public GLRtsViewer(
            VrfGuiContext                                                 vrfGuiContext,
            RendererContext                                               rendererContext,
            string                                                        mapName,
            IReadOnlyList<VisSpan>                                        spans,
            IReadOnlyDictionary<ulong, PlayerInfo>?                       players    = null,
            IReadOnlyDictionary<int, Dictionary<ulong, PlayerTickState>>? ticks      = null,
            IReadOnlyList<RTSSmoke>?                                      smokesList = null,
            IReadOnlyList<RTSDoorEvent>?                                  doorEvents = null)
            : base(vrfGuiContext, rendererContext)
        {
            this.mapName = mapName;
            this.spans   = spans;
            this.players = players    ?? new Dictionary<ulong, PlayerInfo>();
            this.ticks   = ticks      ?? new Dictionary<int, Dictionary<ulong, PlayerTickState>>();
            _doorEvents  = doorEvents ?? [];

            // Build sorted tick id list from whichever source has data
            var tickIds = this.ticks.Count > 0
                ? this.ticks.Keys.OrderBy(t => t).ToList()
                : (IReadOnlyList<int>)Array.Empty<int>();

            sortedTickIds = tickIds;

            // Pre-index smokes by tick — must come after sortedTickIds is assigned
            smokesByTick = BuildSmokesByTick(smokesList ?? []);

            if (spans.Count > 0)
            {
                tickMin = spans.Min(s => s.FirstTick);
                tickMax = spans.Max(s => s.LastTick);
            }
            else if (sortedTickIds.Count > 0)
            {
                tickMin = sortedTickIds[0];
                tickMax = sortedTickIds[^1];
            }

            currentTick = tickMin;
        }

        // ── Scene loading ────────────────────────────────────────────────────

        protected override void LoadScene()
        {
            LoadDefaultLighting();
            try
            {
                var renderContext = Scene.RendererContext;
                var mapResourceName = $"maps/{mapName}.vmap_c";

                var mapResource = renderContext.FileLoader.LoadFile(mapResourceName)
                                  ?? throw new FileNotFoundException($"Failed to load map file '{mapResourceName}'.");

                var worldPath = WorldLoader.GetWorldNameFromMap(mapResourceName);
                var worldResource = renderContext.FileLoader.LoadFileCompiled(worldPath)
                                    ?? throw new FileNotFoundException($"Failed to load world file '{worldPath}'.");

                var world = (World)worldResource.DataBlock!;
                var loader = new WorldLoader(world, Scene);
                loader.Load(mapResource.ExternalReferences, skipVisibility: true, skipEntities: true);
                LoadDoorEntities(world, renderContext.FileLoader);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(GLRtsViewer), $"Failed to load map '{mapName}': {ex.Message}");
            }

            // Build initial overlay on the GL thread immediately after scene load
            RebuildOverlay();
        }

        // ── UI controls ──────────────────────────────────────────────────────

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);

            using (UiControl.BeginGroup("Render"))
            {
                AddRenderModeSelectionControl();
                AddWireframeToggleControl();
            }

            using (UiControl.BeginGroup("RTS Overlay"))
            {
                UiControl.AddCheckBox(
                                      "Visibility Lines",
                                      showVisLines,
                                      v =>
                                      {
                                          showVisLines = v;
                                          overlayDirty = true;
                                      }
                                     );

                UiControl.AddCheckBox(
                                      "Player Capsules",
                                      showPlayers,
                                      v =>
                                      {
                                          showPlayers  = v;
                                          overlayDirty = true;
                                      }
                                     );

                UiControl.AddCheckBox(
                                      "View Direction Rays",
                                      showViewRays,
                                      v =>
                                      {
                                          showViewRays = v;
                                          overlayDirty = true;
                                      }
                                     );

                UiControl.AddCheckBox(
                                      "Smoke Grenades",
                                      showSmokes,
                                      v =>
                                      {
                                          showSmokes   = v;
                                          overlayDirty = true;
                                      }
                                     );

                UiControl.AddCheckBox(
                                      "Doors & Breakables",
                                      _showDoors,
                                      v =>
                                      {
                                          _showDoors   = v;
                                          overlayDirty = true;
                                      }
                                     );

                tickLabel = new Label { Text = TickLabelText(), AutoSize = true, Padding = new Padding(4, 4, 4, 0) };
                UiControl.AddControl(tickLabel);

                spanCountLabel = new Label { Text = SpanCountText(), AutoSize = true, Padding = new Padding(4, 0, 4, 4) };
                UiControl.AddControl(spanCountLabel);

                if (tickMax > tickMin)
                {
                    tickScrubber = UiControl.AddTrackBar(v =>
                                                         {
                                                             currentTick  = tickMin + (int)(v * (tickMax - tickMin));
                                                             overlayDirty = true;
                                                             GLControl?.BeginInvoke(() =>
                                                                                    {
                                                                                        tickLabel?.Text      = TickLabelText();
                                                                                        spanCountLabel?.Text = SpanCountText();
                                                                                    }
                                                                                   );
                                                         }
                                                        );

                    tickScrubber.Slider.Value = 0f;

                    var jumpInput = RendererControl.CreateFloatInput(
                                                                     "Jump to Tick",
                                                                     v => NavigateToTick(Math.Clamp((int)v, tickMin, tickMax)),
                                                                     startValue: tickMin,
                                                                     minValue: tickMin,
                                                                     maxValue: tickMax
                                                                    );

                    UiControl.AddControl(jumpInput);

                    var prevButton = new ThemedButton { Text = "◀ Prev Tick", AutoSize = true };
                    var nextButton = new ThemedButton { Text = "Next Tick ▶", AutoSize = true };
                    prevButton.Click += (_, _) => StepTick(-1);
                    nextButton.Click += (_, _) => StepTick(+1);

                    var buttonPanel = new FlowLayoutPanel
                    {
                        AutoSize      = true,
                        FlowDirection = FlowDirection.LeftToRight,
                        WrapContents  = false,
                    };

                    buttonPanel.Controls.Add(prevButton);
                    buttonPanel.Controls.Add(nextButton);
                    UiControl.AddControl(buttonPanel);
                }
            }

            if (players.Count > 0)
            {
                using (UiControl.BeginGroup("Camera"))
                {
                    var playerLabels = players
                        .OrderBy(kv => kv.Value.Team)
                        .Select(kv =>
                                {
                                    var teamStr = kv.Value.Team switch
                                    {
                                        2 => "T",
                                        3 => "CT",
                                        _ => "?"
                                    };

                                    return $"{kv.Key} [{teamStr}]";
                                }
                               )
                        .ToList();

                    // Populate SteamIds in the same order
                    var orderedSteamIds = players
                        .OrderBy(kv => kv.Value.Team)
                        .Select(kv => kv.Key)
                        .ToList();

                    if (orderedSteamIds.Count > 0)
                        teleportTargetId = orderedSteamIds[0];

                    var playerCombo = UiControl.AddSelection(
                                                             "Target Player",
                                                             (_, idx) =>
                                                             {
                                                                 if (idx >= 0 && idx < orderedSteamIds.Count)
                                                                     teleportTargetId = orderedSteamIds[idx];
                                                             }
                                                            );

                    playerCombo.Items.AddRange([.. playerLabels]);
                    if (playerCombo.Items.Count > 0)
                        playerCombo.SelectedIndex = 0;

                    var teleportButton = new ThemedButton
                    {
                        Text     = "Teleport Camera to Player",
                        AutoSize = true,
                    };

                    teleportButton.Click += (_, _) => TeleportCameraToTarget();
                    UiControl.AddControl(teleportButton);
                }
            }

            base.AddUiControls();
        }

        private void TeleportCameraToTarget()
        {
            var stateMap = GetStateForTick(currentTick);
            if (stateMap == null || !stateMap.TryGetValue(teleportTargetId, out var state))
                return;

            var eyePos = EyePosition(state);
            var yawRad = float.DegreesToRadians(state.VerticalAngle);
            var pitchRad = -float.DegreesToRadians(state.HorizontalAngle);

            Input.SaveCameraForTransition();
            Input.Camera.SetLocationPitchYaw(eyePos, pitchRad, yawRad);
        }

        // Colour for the ray-trace hit marker
        private static readonly Color32 RayHitColor   = new(255, 80, 80, 240); // red   – blocked
        private static readonly Color32 RayClearColor = new(80, 255, 80, 240); // green – clear

        /// <summary>
        /// Fired when a player capsule is clicked. Traces a ray from that player's eye
        /// to every other player's eye and draws colour-coded lines in the scene.
        /// Green = clear, Red = blocked (line ends at first hit).
        /// </summary>
        private void TraceRaysFromPlayer(ulong sourceSteamId)
        {
            // Remove previous click-ray lines
            foreach (var n in clickRayNodes)
                Scene.Remove(n, false);

            clickRayNodes.Clear();
            clickRayLabels.Clear();

            var stateMap = GetStateForTick(currentTick);
            if (stateMap == null || !stateMap.TryGetValue(sourceSteamId, out var srcState))
                return;

            var physics = Scene.PhysicsWorld;
            if (physics == null)
                return;

            var srcEye = EyePosition(srcState);
            var srcViewDir = ViewDirection(srcState);

            foreach (var (targetId, tgtState) in stateMap)
            {
                if (targetId == sourceSteamId)
                    continue;

                var tgtEye = EyePosition(tgtState);
                var trace = physics.TraceRay(srcEye, tgtEye);

                Color32 startColor, endColor;
                Vector3 lineEnd;

                if (trace.Hit)
                {
                    startColor = RayHitColor;
                    endColor   = RayHitColor with { A = 40 };
                    lineEnd    = trace.HitPosition;
                }
                else
                {
                    startColor = RayClearColor;
                    endColor   = RayClearColor with { A = 40 };
                    lineEnd    = tgtEye;
                }

                var line = new LineSceneNode(Scene, srcEye, lineEnd, startColor, endColor)
                {
                    LayerName = "RTS Visibility",
                };

                line.SetInfiniteBounds();
                Scene.Add(line, false);
                clickRayNodes.Add(line);

                // Compute FOV angle between the source's view direction and the direction to target
                var toTarget = Vector3.Normalize(tgtEye - srcEye);
                var dot = Math.Clamp(Vector3.Dot(srcViewDir, toTarget), -1f, 1f);
                var fovDeg = float.RadiansToDegrees(MathF.Acos(dot));
                var midPoint = (srcEye + lineEnd) * 0.5f;
                clickRayLabels.Add((midPoint, $"{fovDeg:F1}°", startColor));
            }
        }

        private string TickLabelText() =>
            tickMax > tickMin
                ? $"Tick: {currentTick}  ({tickMin} – {tickMax})"
                : $"Tick: {currentTick}";

        private string SpanCountText()
        {
            var active = ActiveSpansAt(currentTick).Count();
            return $"Vis spans: {active} / {spans.Count}";
        }

        private void NavigateToTick(int tick)
        {
            currentTick  = tick;
            overlayDirty = true;
            var sliderVal = (tickMax > tickMin)
                ? (float)(tick - tickMin) / (tickMax - tickMin)
                : 0f;

            GLControl?.BeginInvoke(() =>
                                   {
                                       tickScrubber?.Slider.Value = sliderVal;
                                       tickLabel?.Text            = TickLabelText();
                                       spanCountLabel?.Text       = SpanCountText();
                                   }
                                  );
        }

        private void StepTick(int direction)
        {
            if (sortedTickIds.Count == 0)
                return;

            // Find index of the last tick <= currentTick
            var idx = 0;
            for (var i = 0; i < sortedTickIds.Count; i++)
            {
                if (sortedTickIds[i] <= currentTick)
                    idx = i;
                else
                    break;
            }

            // When on an exact tick, both directions must step away from it
            if (sortedTickIds[idx] == currentTick)
                idx += direction;

            NavigateToTick(sortedTickIds[Math.Clamp(idx, 0, sortedTickIds.Count - 1)]);
        }

        // ── Overlay geometry ─────────────────────────────────────────────────

        private IEnumerable<VisSpan> ActiveSpansAt(int tick) =>
            spans.Where(s => tick >= s.FirstTick && tick <= s.LastTick);

        private IReadOnlyList<RTSSmoke> ActiveSmokesAt(int tick) =>
            smokesByTick.TryGetValue(tick, out var list) ? list : [];

        /// <summary>
        /// Builds a lookup from tickId → active smokes for that tick.
        /// A smoke is active at tick T if T &lt; smoke.DestroyTick.
        /// </summary>
        private Dictionary<int, IReadOnlyList<RTSSmoke>> BuildSmokesByTick(IReadOnlyList<RTSSmoke> allSmokes)
        {
            if (allSmokes.Count == 0 || sortedTickIds.Count == 0)
                return new Dictionary<int, IReadOnlyList<RTSSmoke>>();

            var result = new Dictionary<int, IReadOnlyList<RTSSmoke>>(sortedTickIds.Count);
            foreach (var tickId in sortedTickIds)
            {
                var active = allSmokes.Where(s => tickId >= s.SpawnTick && tickId < s.DestroyTick).ToList();
                if (active.Count > 0)
                    result[tickId] = active;
            }

            return result;
        }

        /// <summary>Returns the nearest tick state for a given tick id (exact or closest preceding).</summary>
        private Dictionary<ulong, PlayerTickState>? GetStateForTick(int tick)
        {
            if (ticks.TryGetValue(tick, out var exact))
                return exact;

            // Find closest preceding tick
            var closest = -1;
            foreach (var id in sortedTickIds)
            {
                if (id <= tick) closest = id;
                else break;
            }

            return closest >= 0 && ticks.TryGetValue(closest, out var prev) ? prev : null;
        }

        private static Vector3 EyePosition(PlayerTickState state)
        {
            var duckFraction = state.DuckAmount / 100f;
            var eyeZ = state.FeetPosition.Z + EyeHeightStanding - duckFraction * EyeHeightCrouchDelta;
            return state.FeetPosition with { Z = eyeZ };
        }

        private static Vector3 ViewDirection(PlayerTickState state)
        {
            var yaw = float.DegreesToRadians(state.HorizontalAngle);
            var pitch = float.DegreesToRadians(state.VerticalAngle);
            return new Vector3(
                               MathF.Cos(yaw) * MathF.Cos(pitch),
                               MathF.Cos(yaw) * MathF.Sin(pitch),
                               -MathF.Sin(yaw)
                              );
        }

        private Color32 ColorForPlayer(ulong steamId)
        {
            if (players.TryGetValue(steamId, out var info))
            {
                return info.Team switch
                {
                    2 => ColorT,
                    3 => ColorCT,
                    _ => ColorUnknown
                };
            }

            return ColorUnknown;
        }

        /// <summary>
        /// Adds two short lines forming a "V" arrowhead at <paramref name="tip"/>,
        /// pointing in the direction from <paramref name="tail"/> to <paramref name="tip"/>.
        /// </summary>
        private void AddArrowhead(Vector3 tail, Vector3 tip, Color32 color)
        {
            const float ArrowLength = 12f; // units along the shaft
            const float ArrowHalfWidth = 6f; // half-spread perpendicular to shaft

            var forward = Vector3.Normalize(tail - tip); // points back along shaft

            // Choose a perpendicular axis: prefer world-up; fall back to world-right if parallel
            var up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitZ)) < 0.99f
                ? Vector3.UnitZ
                : Vector3.UnitX;

            var right = Vector3.Normalize(Vector3.Cross(forward, up));

            var base1 = tip                         + forward * ArrowLength + right * ArrowHalfWidth;
            var base2 = tip + forward * ArrowLength - right   * ArrowHalfWidth;

            var arm1 = new LineSceneNode(Scene, tip, base1, color, color) { LayerName = "RTS Visibility" };
            var arm2 = new LineSceneNode(Scene, tip, base2, color, color) { LayerName = "RTS Visibility" };

            arm1.SetInfiniteBounds();
            arm2.SetInfiniteBounds();

            Scene.Add(arm1, false);
            Scene.Add(arm2, false);
            overlayNodes.Add(arm1);
            overlayNodes.Add(arm2);
        }

        private void RebuildOverlay()
        {
            foreach (var node in overlayNodes)
                Scene.Remove(node, false);

            overlayNodes.Clear();

            foreach (var node in clickRayNodes)
                Scene.Remove(node, false);

            clickRayNodes.Clear();
            clickRayLabels.Clear();
            playerSteamIdLabels.Clear();

            var stateMap = GetStateForTick(currentTick);
            var activeSpans = ActiveSpansAt(currentTick).ToList();

            // Collect IDs involved in any active vis span so their capsules can glow
            var glowingIds = new HashSet<ulong>();
            foreach (var span in activeSpans)
            {
                glowingIds.Add(span.SourceId);
                glowingIds.Add(span.TargetId);
            }

            // ── Player capsules + view rays ──────────────────────────────
            capsuleSteamIds.Clear();
            if (stateMap != null)
            {
                foreach (var (steamId, state) in stateMap)
                {
                    var eyePos = EyePosition(state);
                    var glow = glowingIds.Contains(steamId);
                    var color = ColorForPlayer(steamId);

                    if (showPlayers)
                    {
                        var duckFraction = state.DuckAmount / 100f;
                        var capsuleHalf = PlayerHalfHeight - duckFraction * (PlayerHalfHeight * 0.5f);
                        var bottom = state.FeetPosition with { Z = state.FeetPosition.Z + capsuleHalf };
                        var top = new Vector3(state.FeetPosition.X, state.FeetPosition.Y, eyePos.Z);
                        var capsule = new CapsuleSceneNode(Scene, bottom, top, PlayerRadius, color)
                        {
                            LayerName  = "RTS Players",
                            IsSelected = glow,
                        };

                        capsule.SetInfiniteBounds();

                        Scene.Add(capsule, false);
                        overlayNodes.Add(capsule);
                        capsuleSteamIds[capsule] = steamId;

                        // Label above the capsule showing the SteamId
                        var labelPos = new Vector3(state.FeetPosition.X, state.FeetPosition.Y, eyePos.Z + PlayerRadius + 10f);
                        playerSteamIdLabels.Add((labelPos, steamId.ToString(), color));
                    }

                    if (showViewRays)
                    {
                        var dir = ViewDirection(state) * ViewRayLength;
                        var ray = new LineSceneNode(Scene, eyePos, eyePos + dir, ViewRayColor, ViewRayColor with { A = 0 })
                        {
                            LayerName = "RTS View Rays",
                        };

                        ray.SetInfiniteBounds();

                        Scene.Add(ray, false);
                        overlayNodes.Add(ray);
                    }
                }
            }

            // ── Visibility span lines ────────────────────────────────────
            if (showVisLines)
            {
                // Build a set of one-way visibility pairs present this tick for reciprocal detection
                var visSet = new HashSet<(ulong, ulong)>(activeSpans.Select(s => (s.SourceId, s.TargetId)));

                foreach (var span in activeSpans)
                {
                    // Resolve positions — fall back to zero if a player state is missing
                    var srcPos = stateMap != null && stateMap.TryGetValue(span.SourceId, out var srcState)
                        ? EyePosition(srcState)
                        : Vector3.Zero;

                    var tgtPos = stateMap != null && stateMap.TryGetValue(span.TargetId, out var tgtState)
                        ? EyePosition(tgtState)
                        : Vector3.Zero;

                    // Skip degenerate lines (both ends unknown/same point)
                    if (srcPos == tgtPos)
                        continue;

                    var line = new LineSceneNode(Scene, srcPos, tgtPos, VisLineColor, VisLineColor)
                    {
                        LayerName = "RTS Visibility",
                    };

                    line.SetInfiniteBounds();
                    Scene.Add(line, false);
                    overlayNodes.Add(line);

                    // Arrowhead at target (source→target direction)
                    AddArrowhead(srcPos, tgtPos, VisLineColor);

                    // If target also sees source, add arrowhead at the source end too
                    if (visSet.Contains((span.TargetId, span.SourceId)))
                    {
                        AddArrowhead(tgtPos, srcPos, VisLineColor);
                    }
                }
            }

            // ── Smoke spheres ────────────────────────────────────────────
            if (showSmokes)
            {
                var activeSmokes = ActiveSmokesAt(currentTick);
                foreach (var smoke in activeSmokes)
                {
                    var sphere = new SphereSceneNode(Scene, smoke.Position, SmokeRadius, SmokeColor)
                    {
                        LayerName = "RTS Smokes",
                    };

                    sphere.SetInfiniteBounds();
                    Scene.Add(sphere, false);
                    overlayNodes.Add(sphere);
                }
            }

            // ── Doors and breakables ──────────────────────────────────────
            UpdateDoorTransforms();
        }

        // ── Frame update

        private sealed class DoorEntity
        {
            public required Vector3         Origin     { get; init; }
            public required Vector3         BaseAngles { get; init; }
            public required PhysSceneNode[] Nodes      { get; init; }
        }

        private void LoadDoorEntities(World world, GameFileLoader fileLoader)
        {
            foreach (var lumpName in world.GetEntityLumpNames())
            {
                var lumpResource = fileLoader.LoadFileCompiled(lumpName);
                if (lumpResource?.DataBlock is not EntityLump entityLump)
                    continue;

                CollectDoorEntitiesFromLump(entityLump, fileLoader);
            }
        }

        private void CollectDoorEntitiesFromLump(EntityLump entityLump, GameFileLoader fileLoader)
        {
            foreach (var childName in entityLump.GetChildEntityNames())
            {
                var childResource = fileLoader.LoadFileCompiled(childName);
                if (childResource?.DataBlock is not EntityLump childLump)
                    continue;

                CollectDoorEntitiesFromLump(childLump, fileLoader);
            }

            foreach (var entity in entityLump.GetEntities())
            {
                var classname = entity.GetStringProperty("classname");
                if (classname is not ("prop_door_rotating" or "func_breakable"))
                    continue;

                var modelPath = entity.GetStringProperty("model");
                if (string.IsNullOrEmpty(modelPath))
                    continue;

                var modelResource = fileLoader.LoadFileCompiled(modelPath);
                if (modelResource?.DataBlock is not Model model)
                    continue;

                PhysAggregateData? phys = model.GetEmbeddedPhys();
                if (phys == null)
                {
                    var refPhysName = model.GetReferencedPhysNames().FirstOrDefault();
                    if (refPhysName != null)
                        phys = fileLoader.LoadFileCompiled(refPhysName)?.DataBlock as PhysAggregateData;
                }

                if (phys == null || phys.Parts.Length == 0)
                    continue;

                var origin = entity.GetVector3Property("origin");
                var angles = entity.GetVector3Property("angles");

                var nodes = PhysSceneNode.CreatePhysSceneNodes(Scene, phys, modelPath, classname).ToArray();
                if (nodes.Length == 0)
                    continue;

                var transform = CreateDoorTransform(origin, angles);
                foreach (var node in nodes)
                {
                    node.Transform = transform;
                    node.LayerName = "RTS Doors";
                    Scene.Add(node, false);
                }

                _doorEntities.Add(new DoorEntity { Origin = origin, BaseAngles = angles, Nodes = nodes });
            }
        }

        private static Matrix4x4 CreateDoorTransform(Vector3 origin, Vector3 pitchYawRoll)
        {
            var rotation = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(pitchYawRoll);
            return rotation * Matrix4x4.CreateTranslation(origin);
        }

        private void UpdateDoorTransforms()
        {
            if (_doorEntities.Count == 0)
                return;

            var currentAngles = new Vector3[_doorEntities.Count];
            var deleted = new bool[_doorEntities.Count];

            for (var i = 0; i < _doorEntities.Count; i++)
                currentAngles[i] = _doorEntities[i].BaseAngles;

            // Replay events up to currentTick — events are sorted ascending by tick
            foreach (var ev in _doorEvents)
            {
                if (ev.Tick > currentTick)
                    break;

                var idx = FindClosestDoorEntity(new Vector3(ev.X, ev.Y, ev.Z));
                if (idx < 0)
                    continue;

                if (ev.EventName == "moved")
                    currentAngles[idx] = new Vector3(ev.AngX, ev.AngY, 0f);
                else if (ev.EventName is "damaged" or "deleted")
                    deleted[idx] = true;
            }

            for (var i = 0; i < _doorEntities.Count; i++)
            {
                var visible = _showDoors && !deleted[i];
                var transform = CreateDoorTransform(_doorEntities[i].Origin, currentAngles[i]);
                foreach (var node in _doorEntities[i].Nodes)
                {
                    node.Transform = transform;
                    node.Enabled   = visible;
                }
            }
        }

        private int FindClosestDoorEntity(Vector3 position)
        {
            const float Tolerance = 100f;
            var best = -1;
            var bestDist = float.MaxValue;
            for (var i = 0; i < _doorEntities.Count; i++)
            {
                var dist = Vector3.Distance(_doorEntities[i].Origin, position);
                if (dist < Tolerance && dist < bestDist)
                {
                    bestDist = dist;
                    best     = i;
                }
            }

            return best;
        }

        protected override void OnUpdate(float frameTime)
        {
            base.OnUpdate(frameTime);

            if (overlayDirty)
            {
                overlayDirty = false;
                RebuildOverlay();
            }
        }

        protected override void OnPaint(float frameTime)
        {
            base.OnPaint(frameTime);

            foreach (var (worldPos, label, color) in playerSteamIdLabels)
            {
                TextRenderer.AddTextBillboard(
                                              worldPos,
                                              new ValveResourceFormat.Renderer.TextRenderer.TextRenderRequest
                                              {
                                                  Text             = label,
                                                  Scale            = 8f,
                                                  Color            = color,
                                                  CenterHorizontal = true,
                                                  CenterVertical   = true,
                                              },
                                              Renderer.Camera
                                             );
            }

            foreach (var (worldPos, label, color) in clickRayLabels)
            {
                TextRenderer.AddTextBillboard(
                                              worldPos,
                                              new ValveResourceFormat.Renderer.TextRenderer.TextRenderRequest
                                              {
                                                  Text             = label,
                                                  Scale            = 16f,
                                                  Color            = color,
                                                  CenterHorizontal = true,
                                                  CenterVertical   = true,
                                              },
                                              Renderer.Camera
                                             );
            }
        }

        // ── Required abstract overrides ──────────────────────────────────────

        protected override void OnPicked(object? sender, PickingResponse pickingResponse)
        {
            var pixelInfo = pickingResponse.PixelInfo;
            if (pixelInfo.ObjectId == 0)
                return;

            var node = Scene.Find(pixelInfo.ObjectId);
            if (node == null || !capsuleSteamIds.TryGetValue(node, out var steamId))
                return;

            // Copy SteamId to clipboard — must run on the UI (STA) thread
            GLControl?.Invoke(() => Clipboard.SetText(steamId.ToString()));

            TraceRaysFromPlayer(steamId);
        }

        public override void Dispose()
        {
            base.Dispose();
            tickLabel?.Dispose();
            tickLabel = null;
            spanCountLabel?.Dispose();
            spanCountLabel = null;
            tickScrubber?.Dispose();
            tickScrubber = null;
            clickRayNodes.Clear();
            capsuleSteamIds.Clear();
        }
    }
}
