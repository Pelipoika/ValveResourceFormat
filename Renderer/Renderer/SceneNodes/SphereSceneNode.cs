namespace ValveResourceFormat.Renderer.SceneNodes
{
    /// <summary>
    /// A simple sphere shape scene node used for smoke grenade visualization in RTS overlays.
    /// </summary>
    public class SphereSceneNode : ShapeSceneNode
    {
        /// <summary>Creates a sphere at <paramref name="center"/> with the given radius and colour.</summary>
        public SphereSceneNode(Scene scene, Vector3 center, float radius, Color32 color)
            : base(scene, center, radius, color)
        {
        }
    }
}
