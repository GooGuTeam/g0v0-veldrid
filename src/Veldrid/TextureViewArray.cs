using System;

namespace Veldrid
{
    /// <summary>
    /// An immutable list of sampled texture views occupying one array binding in a Vulkan resource set.
    /// The caller retains ownership of the views and their textures, as with scalar resource bindings.
    /// </summary>
    public sealed class TextureViewArray : IBindableResource
    {
        private readonly TextureView[] views;

        /// <summary>The number of descriptors supplied by this binding.</summary>
        public uint Count => (uint)views.Length;

        /// <summary>Gets a view in descriptor order.</summary>
        public TextureView this[int index] => views[index];

        /// <summary>Copies a non-empty list of texture views for a resource set binding.</summary>
        /// <param name="views">Views in shader array index order.</param>
        public TextureViewArray(params TextureView[] views)
        {
            ArgumentNullException.ThrowIfNull(views);
            if (views.Length == 0)
                throw new ArgumentException("A texture array must contain at least one view.", nameof(views));

            this.views = (TextureView[])views.Clone();
            foreach (var view in this.views)
            {
                ArgumentNullException.ThrowIfNull(view);
                if ((view.Target.Usage & TextureUsage.Sampled) == 0)
                    throw new ArgumentException("Every array view must reference a sampled texture.", nameof(views));
            }
        }
    }
}
