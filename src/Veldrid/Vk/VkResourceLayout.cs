using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;

namespace Veldrid.Vk
{
    internal unsafe class VkResourceLayout : ResourceLayout
    {
        public VkDescriptorSetLayout DescriptorSetLayout => dsl;
        public VkDescriptorType[] DescriptorTypes { get; }
        public uint[] DescriptorCounts { get; }

        public DescriptorResourceCounts DescriptorResourceCounts { get; }
        public new int DynamicBufferCount { get; }

        public override bool IsDisposed => disposed;

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                gd.SetResourceName(this, value);
            }
        }

        private readonly VkGraphicsDevice gd;
        private readonly VkDescriptorSetLayout dsl;
        private bool disposed;
        private string name;

        public VkResourceLayout(VkGraphicsDevice gd, ref ResourceLayoutDescription description)
            : base(ref description, supportsTextureArrays: true)
        {
            this.gd = gd;
            var dslCi = VkDescriptorSetLayoutCreateInfo.New();
            var elements = description.Elements;
            DescriptorTypes = new VkDescriptorType[elements.Length];
            DescriptorCounts = new uint[elements.Length];
            var bindings = stackalloc VkDescriptorSetLayoutBinding[elements.Length];

            uint uniformBufferCount = 0;
            uint uniformBufferDynamicCount = 0;
            uint sampledImageCount = 0;
            uint samplerCount = 0;
            uint storageBufferCount = 0;
            uint storageBufferDynamicCount = 0;
            uint storageImageCount = 0;

            for (uint i = 0; i < elements.Length; i++)
            {
                bindings[i] = default;
                bindings[i].binding = i;
                bindings[i].descriptorCount = elements[i].DescriptorCount;
                DescriptorCounts[i] = elements[i].DescriptorCount;
                var descriptorType = VkFormats.VdToVkDescriptorType(elements[i].Kind, elements[i].Options);
                bindings[i].descriptorType = descriptorType;
                bindings[i].stageFlags = VkFormats.VdToVkShaderStages(elements[i].Stages);
                if ((elements[i].Options & ResourceLayoutElementOptions.DynamicBinding) != 0) DynamicBufferCount += 1;

                DescriptorTypes[i] = descriptorType;

                switch (descriptorType)
                {
                    case VkDescriptorType.Sampler:
                        samplerCount += 1;
                        break;

                    case VkDescriptorType.SampledImage:
                        sampledImageCount = checked(sampledImageCount + elements[i].DescriptorCount);
                        break;

                    case VkDescriptorType.StorageImage:
                        storageImageCount += 1;
                        break;

                    case VkDescriptorType.UniformBuffer:
                        uniformBufferCount += 1;
                        break;

                    case VkDescriptorType.UniformBufferDynamic:
                        uniformBufferDynamicCount += 1;
                        break;

                    case VkDescriptorType.StorageBuffer:
                        storageBufferCount += 1;
                        break;

                    case VkDescriptorType.StorageBufferDynamic:
                        storageBufferDynamicCount += 1;
                        break;
                }
            }

            DescriptorResourceCounts = new DescriptorResourceCounts(
                uniformBufferCount,
                uniformBufferDynamicCount,
                sampledImageCount,
                samplerCount,
                storageBufferCount,
                storageBufferDynamicCount,
                storageImageCount);

            if (sampledImageCount > gd.SampledTextureArrayLimit)
                throw new VeldridException($"Sampled texture count {sampledImageCount} exceeds this device's array limit {gd.SampledTextureArrayLimit}.");

            dslCi.bindingCount = (uint)elements.Length;
            dslCi.pBindings = bindings;

            var result = vkCreateDescriptorSetLayout(this.gd.Device, ref dslCi, null, out dsl);
            CheckResult(result);
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                vkDestroyDescriptorSetLayout(gd.Device, dsl, null);
            }
        }

        #endregion
    }
}
