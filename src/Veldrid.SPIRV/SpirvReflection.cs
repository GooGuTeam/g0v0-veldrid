namespace Veldrid.SPIRV
{
    /// <summary>
    /// Contains information about the vertex attributes and resource types, and their binding slots, for a compiled
    /// set of shaders. This information can be used to construct <see cref="ResourceLayout"/> and
    /// <see cref="Pipeline"/> objects.
    /// </summary>
    public class SpirvReflection
    {
        /// <summary>
        /// An array containing a description of each vertex element that is used by the compiled shader set.
        /// This array will be empty for compute shaders.
        /// </summary>
        public VertexElementDescription[] VertexElements { get; }

        /// <summary>
        /// An array containing a description of each set of resources used by the compiled shader set.
        /// </summary>
        public ResourceLayoutDescription[] ResourceLayouts { get; }

        /// <summary>
        /// Constructs a new <see cref="SpirvReflection"/> instance.
        /// </summary>
        /// <param name="vertexElements">/// An array containing a description of each vertex element that is used by
        /// the compiled shader set.</param>
        /// <param name="resourceLayouts">An array containing a description of each set of resources used by the
        /// compiled shader set.</param>
        public SpirvReflection(
            VertexElementDescription[] vertexElements,
            ResourceLayoutDescription[] resourceLayouts)
        {
            VertexElements = vertexElements;
            ResourceLayouts = resourceLayouts;
        }
    }
}