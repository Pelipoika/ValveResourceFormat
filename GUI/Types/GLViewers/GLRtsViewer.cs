using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Utils;
using ValveResourceFormat.Renderer.World;
using static ValveResourceFormat.Renderer.PickingTexture;

namespace GUI.Types.GLViewers
{
    internal class GLRtsViewer : GLSceneViewer
    {
        // ── Public data types ────────────────────────────────────────────────

        public sealed record VisSpan(int FirstTick, int LastTick, ulong SourceId, ulong TargetId);

        public sealed record PlayerInfo(ulong SteamId, uint Team);

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

        private readonly string             mapName;
        private readonly int                tickMin;
        private readonly int                tickMax;
        private readonly IReadOnlyList<int> sortedTickIds;

        // ── Runtime state ────────────────────────────────────────────────────

        private          int  currentTick;
        private          bool showVisLines = true;
        private          bool showPlayers  = true;
        private          bool showViewRays;
        private volatile bool overlayDirty;

        private readonly List<SceneNode> overlayNodes = [];

        private Label?                 tickLabel;
        private Label?                 spanCountLabel;
        private GLViewerSliderControl? tickScrubber;

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

        // ── Constructor ──────────────────────────────────────────────────────

        public GLRtsViewer(
            VrfGuiContext                                                 vrfGuiContext,
            RendererContext                                               rendererContext,
            string                                                        mapName,
            IReadOnlyList<VisSpan>                                        spans,
            IReadOnlyDictionary<ulong, PlayerInfo>?                       players = null,
            IReadOnlyDictionary<int, Dictionary<ulong, PlayerTickState>>? ticks   = null)
            : base(vrfGuiContext, rendererContext)
        {
            this.mapName = mapName;
            this.spans   = spans;
            this.players = players ?? new Dictionary<ulong, PlayerInfo>();
            this.ticks   = ticks   ?? new Dictionary<int, Dictionary<ulong, PlayerTickState>>();

            // Build sorted tick id list from whichever source has data
            var tickIds = this.ticks.Count > 0
                ? this.ticks.Keys.OrderBy(t => t).ToList()
                : (IReadOnlyList<int>)Array.Empty<int>();

            sortedTickIds = tickIds;

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

                var loader = new WorldLoader((ValveResourceFormat.ResourceTypes.World)worldResource.DataBlock!, Scene);
                loader.Load(mapResource.ExternalReferences, skipVisibility: true, skipEntities: true);
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
                                                                     v =>
                                                                     {
                                                                         var tick = Math.Clamp((int)v, tickMin, tickMax);
                                                                         currentTick  = tick;
                                                                         overlayDirty = true;
                                                                         var sliderVal = (tickMax > tickMin)
                                                                             ? (float)(tick - tickMin) / (tickMax - tickMin)
                                                                             : 0f;

                                                                         GLControl?.BeginInvoke(() =>
                                                                                                {
                                                                                                    if (tickScrubber != null)
                                                                                                        tickScrubber.Slider.Value = sliderVal;

                                                                                                    tickLabel?.Text      = TickLabelText();
                                                                                                    spanCountLabel?.Text = SpanCountText();
                                                                                                }
                                                                                               );
                                                                     },
                                                                     startValue: tickMin,
                                                                     minValue: tickMin,
                                                                     maxValue: tickMax
                                                                    );

                    UiControl.AddControl(jumpInput);
                }
            }

            base.AddUiControls();
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

        // ── Overlay geometry ─────────────────────────────────────────────────

        private IEnumerable<VisSpan> ActiveSpansAt(int tick) =>
            spans.Where(s => tick >= s.FirstTick && tick <= s.LastTick);

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
                        var bottom = new Vector3(state.FeetPosition.X, state.FeetPosition.Y, state.FeetPosition.Z + capsuleHalf);
                        var top = new Vector3(state.FeetPosition.X,    state.FeetPosition.Y, eyePos.Z);
                        var capsule = new CapsuleSceneNode(Scene, bottom, top, PlayerRadius, color)
                        {
                            LayerName  = "RTS Players",
                            IsSelected = glow,
                        };

                        capsule.SetInfiniteBounds();

                        Scene.Add(capsule, false);
                        overlayNodes.Add(capsule);
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
        }

        // ── Frame update

        protected override void OnUpdate(float frameTime)
        {
            base.OnUpdate(frameTime);

            if (overlayDirty)
            {
                overlayDirty = false;
                RebuildOverlay();
            }
        }

        // ── Required abstract overrides ──────────────────────────────────────

        protected override void OnPicked(object? sender, PickingResponse pixelInfo)
        {
            // No picking interaction for this viewer
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
        }
    }
}
