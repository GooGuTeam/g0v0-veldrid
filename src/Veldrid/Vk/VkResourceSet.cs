using System.Collections.Generic;
using System;
using Vulkan;
using static Vulkan.VulkanNative;

namespace Veldrid.Vk
{
    internal unsafe class VkResourceSet : ResourceSet
    {
        public VkDescriptorSet DescriptorSet => descriptorAllocationToken.Set;
        public List<VkTexture> SampledTextures { get; } = new List<VkTexture>();

        public List<VkTexture> StorageTextures { get; } = new List<VkTexture>();

        public ResourceRefCount RefCount { get; }
        public List<ResourceRefCount> RefCounts { get; } = new List<ResourceRefCount>();

        public override bool IsDisposed => destroyed;

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
        private readonly DescriptorResourceCounts descriptorCounts;
        private readonly DescriptorAllocationToken descriptorAllocationToken;

        private bool destroyed;
        private string name;

        public VkResourceSet(VkGraphicsDevice gd, ref ResourceSetDescription description)
            : base(ref description)
        {
            this.gd = gd;
            RefCount = new ResourceRefCount(disposeCore);
            var vkLayout = Util.AssertSubtype<ResourceLayout, VkResourceLayout>(description.Layout);

            var boundResources = description.BoundResources;
            if (boundResources.Length != vkLayout.DescriptorCounts.Length)
                throw new VeldridException("Resource binding count does not match the layout.");
            int imageInfoCount = 0;
            for (int i = 0; i < boundResources.Length; i++)
            {
                uint count = vkLayout.DescriptorCounts[i];
                if (count > 1 && (boundResources[i] is not TextureViewArray array || array.Count != count))
                    throw new VeldridException($"Texture array in binding {i} must contain {count} views.");
                if (count == 1 && boundResources[i] is TextureViewArray)
                    throw new VeldridException("A scalar texture binding requires a texture or texture view.");
                imageInfoCount = checked(imageInfoCount + (int)count);
            }

            var dsl = vkLayout.DescriptorSetLayout;
            descriptorCounts = vkLayout.DescriptorResourceCounts;
            descriptorAllocationToken = this.gd.DescriptorPoolManager.Allocate(descriptorCounts, dsl);

            uint descriptorWriteCount = (uint)boundResources.Length;
            try
            {
                Span<VkWriteDescriptorSet> writesStorage = descriptorWriteCount <= 64 ? stackalloc VkWriteDescriptorSet[(int)descriptorWriteCount] : new VkWriteDescriptorSet[descriptorWriteCount];
                Span<VkDescriptorBufferInfo> buffersStorage = descriptorWriteCount <= 64 ? stackalloc VkDescriptorBufferInfo[(int)descriptorWriteCount] : new VkDescriptorBufferInfo[descriptorWriteCount];
                Span<VkDescriptorImageInfo> imagesStorage = imageInfoCount <= 64 ? stackalloc VkDescriptorImageInfo[imageInfoCount] : new VkDescriptorImageInfo[imageInfoCount];
                writesStorage.Clear();
                buffersStorage.Clear();
                imagesStorage.Clear();
                fixed (VkWriteDescriptorSet* descriptorWrites = writesStorage)
                fixed (VkDescriptorBufferInfo* bufferInfos = buffersStorage)
                fixed (VkDescriptorImageInfo* imageInfos = imagesStorage)
                {
                    int imageIndex = 0;

                    for (int i = 0; i < descriptorWriteCount; i++)
                    {
                        var type = vkLayout.DescriptorTypes[i];

                        descriptorWrites[i].sType = VkStructureType.WriteDescriptorSet;
                        descriptorWrites[i].descriptorCount = vkLayout.DescriptorCounts[i];
                        descriptorWrites[i].descriptorType = type;
                        descriptorWrites[i].dstBinding = (uint)i;
                        descriptorWrites[i].dstSet = descriptorAllocationToken.Set;

                        if (type == VkDescriptorType.UniformBuffer || type == VkDescriptorType.UniformBufferDynamic
                                                                   || type == VkDescriptorType.StorageBuffer || type == VkDescriptorType.StorageBufferDynamic)
                        {
                            var range = Util.GetBufferRange(boundResources[i], 0);
                            var rangedVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(range.Buffer);
                            bufferInfos[i].buffer = rangedVkBuffer.DeviceBuffer;
                            bufferInfos[i].offset = range.Offset;
                            bufferInfos[i].range = range.SizeInBytes;
                            descriptorWrites[i].pBufferInfo = &bufferInfos[i];
                            RefCounts.Add(rangedVkBuffer.RefCount);
                        }
                        else if (type == VkDescriptorType.SampledImage)
                        {
                            descriptorWrites[i].pImageInfo = &imageInfos[imageIndex];
                            for (int j = 0; j < vkLayout.DescriptorCounts[i]; j++)
                            {
                                var texView = boundResources[i] is TextureViewArray array ? array[j] : Util.GetTextureView(this.gd, boundResources[i]);
                                var vkTexView = Util.AssertSubtype<TextureView, VkTextureView>(texView);
                                imageInfos[imageIndex].imageView = vkTexView.ImageView;
                                imageInfos[imageIndex++].imageLayout = VkImageLayout.ShaderReadOnlyOptimal;
                                SampledTextures.Add(Util.AssertSubtype<Texture, VkTexture>(texView.Target));
                                RefCounts.Add(vkTexView.RefCount);
                            }
                        }
                        else if (type == VkDescriptorType.StorageImage)
                        {
                            var texView = Util.GetTextureView(this.gd, boundResources[i]);
                            var vkTexView = Util.AssertSubtype<TextureView, VkTextureView>(texView);
                            imageInfos[imageIndex].imageView = vkTexView.ImageView;
                            imageInfos[imageIndex].imageLayout = VkImageLayout.General;
                            descriptorWrites[i].pImageInfo = &imageInfos[imageIndex++];
                            StorageTextures.Add(Util.AssertSubtype<Texture, VkTexture>(texView.Target));
                            RefCounts.Add(vkTexView.RefCount);
                        }
                        else if (type == VkDescriptorType.Sampler)
                        {
                            var sampler = Util.AssertSubtype<IBindableResource, VkSampler>(boundResources[i]);
                            imageInfos[imageIndex].sampler = sampler.DeviceSampler;
                            descriptorWrites[i].pImageInfo = &imageInfos[imageIndex++];
                            RefCounts.Add(sampler.RefCount);
                        }
                    }

                    vkUpdateDescriptorSets(this.gd.Device, descriptorWriteCount, descriptorWrites, 0, null);
                }
            }
            catch
            {
                this.gd.DescriptorPoolManager.Free(descriptorAllocationToken, descriptorCounts);
                throw;
            }
        }

        #region Disposal

        public override void Dispose()
        {
            RefCount.Decrement();
        }

        #endregion

        private void disposeCore()
        {
            if (!destroyed)
            {
                destroyed = true;
                gd.DescriptorPoolManager.Free(descriptorAllocationToken, descriptorCounts);
            }
        }
    }
}
