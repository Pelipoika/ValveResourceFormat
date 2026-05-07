namespace ValveResourceFormat.Renderer.SceneNodes
{
    /// <summary>
    /// A simple capsule shape scene node used for player visualization in RTS overlays.
    /// </summary>
    public class CapsuleSceneNode : ShapeSceneNode
    {
        /// <summary>Creates a capsule between two end-cap centres with the given radius and colour.</summary>
        public CapsuleSceneNode(Scene scene, Vector3 from, Vector3 to, float radius, Color32 color)
            : base(scene, from, to, radius, color)
        {
        }
    }
}
