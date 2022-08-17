using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ryujinx.Graphics.Shader
{
    public class ShaderProgramInfo
    {
        public ReadOnlyCollection<BufferDescriptor> CBuffers { get; }
        public ReadOnlyCollection<BufferDescriptor> SBuffers { get; }
        public ReadOnlyCollection<TextureDescriptor> Textures { get; }
        public ReadOnlyCollection<TextureDescriptor> Images { get; }

        public ShaderStage Stage { get; }
        public bool UsesInstanceId { get; }
        public bool UsesRtLayer { get; }
        public byte ClipDistancesWritten { get; }
        public int FragmentOutputMap { get; }

        public ShaderProgramInfo(
            BufferDescriptor[] cBuffers,
            BufferDescriptor[] sBuffers,
            TextureDescriptor[] textures,
            TextureDescriptor[] images,
            ShaderStage stage,
            bool usesInstanceId,
            bool usesRtLayer,
            byte clipDistancesWritten,
            int fragmentOutputMap)
        {
            CBuffers = Array.AsReadOnly(cBuffers);
            SBuffers = Array.AsReadOnly(sBuffers);
            Textures = Array.AsReadOnly(textures);
            Images = Array.AsReadOnly(images);

            Stage = stage;
            UsesInstanceId = usesInstanceId;
            UsesRtLayer = usesRtLayer;
            ClipDistancesWritten = clipDistancesWritten;
            FragmentOutputMap = fragmentOutputMap;
        }

        public override string ToString()
        {
            List<string> results = new() {$"Stage={Stage}"};
            if (CBuffers.Any()) results.Add($"{nameof(CBuffers)}=[{string.Join(", ", CBuffers)}]");
            if (SBuffers.Any()) results.Add($"{nameof(SBuffers)}=[{string.Join(", ", SBuffers)}]");
            if (Textures.Any()) results.Add($"{nameof(Textures)}=[{string.Join(", ", Textures)}]");
            if (Images.Any()) results.Add($"{nameof(Images)}=[{string.Join(", ", Images)}]");
            return string.Join(" ", results);
        }
    }
}