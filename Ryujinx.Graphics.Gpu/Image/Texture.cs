using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Graphics.Texture.Astc;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// Represents a cached GPU texture.
    /// </summary>
    class Texture : IMultiRangeItem, IDisposable
    {
        // How many updates we need before switching to the byte-by-byte comparison
        // modification check method.
        // This method uses much more memory so we want to avoid it if possible.
        private const int ByteComparisonSwitchThreshold = 4;

        private const int MinLevelsForForceAnisotropy = 5;

        private struct TexturePoolOwner
        {
            public TexturePool Pool;
            public int ID;
        }

        private GpuContext _context;
        private PhysicalMemory _physicalMemory;

        private SizeInfo _sizeInfo;

        /// <summary>
        /// Texture format.
        /// </summary>
        public Format Format => Info.FormatInfo.Format;

        /// <summary>
        /// Texture target.
        /// </summary>
        public Target Target { get; private set; }

        /// <summary>
        /// Texture width.
        /// </summary>
        public int Width { get; private set; }

        /// <summary>
        /// Texture height.
        /// </summary>
        public int Height { get; private set; }

        /// <summary>
        /// Texture information.
        /// </summary>
        public TextureInfo Info { get; private set; }

        /// <summary>
        /// Set when anisotropic filtering can be forced on the given texture.
        /// </summary>
        public bool CanForceAnisotropy { get; private set; }

        /// <summary>
        /// Host scale factor.
        /// </summary>
        public float ScaleFactor { get; private set; }

        /// <summary>
        /// Upscaling mode. Informs if a texture is scaled, or is eligible for scaling.
        /// </summary>
        public TextureScaleMode ScaleMode { get; private set; }

        /// <summary>
        /// Group that this texture belongs to. Manages read/write memory tracking.
        /// </summary>
        public TextureGroup Group { get; private set; }

        /// <summary>
        /// Set when a texture has been changed size. This indicates that it may need to be
        /// changed again when obtained as a sampler.
        /// </summary>
        public bool ChangedSize { get; private set; }

        /// <summary>
        /// Set when a texture's GPU VA has ever been partially or fully unmapped.
        /// This indicates that the range must be fully checked when matching the texture.
        /// </summary>
        public bool ChangedMapping { get; private set; }

        /// <summary>
        /// True if the data for this texture must always be flushed when an overlap appears.
        /// This is useful if SetData is called directly on this texture, but the data is meant for a future texture.
        /// </summary>
        public bool AlwaysFlushOnOverlap { get; private set; }

        /// <summary>
        /// Increments when the host texture is swapped, or when the texture is removed from all pools.
        /// </summary>
        public int InvalidatedSequence { get; private set; }

        private int _depth;
        private int _layers;
        public int FirstLayer { get; private set; }
        public int FirstLevel { get; private set; }

        private bool _hasData;
        private bool _dirty = true;
        private int _updateCount;
        private byte[] _currentData;

        private bool _modifiedStale = true;

        private ITexture _arrayViewTexture;
        private Target   _arrayViewTarget;

        private ITexture _flushHostTexture;

        private Texture _viewStorage;

        private List<Texture> _views;

        /// <summary>
        /// Host texture.
        /// </summary>
        public ITexture HostTexture { get; private set; }

        /// <summary>
        /// Intrusive linked list node used on the auto deletion texture cache.
        /// </summary>
        public LinkedListNode<Texture> CacheNode { get; set; }

        /// <summary>
        /// Event to fire when texture data is disposed.
        /// </summary>
        public event Action<Texture> Disposed;

        /// <summary>
        /// Physical memory ranges where the texture data is located.
        /// </summary>
        public MultiRange Range { get; private set; }

        /// <summary>
        /// Layer size in bytes.
        /// </summary>
        public int LayerSize => _sizeInfo.LayerSize;

        /// <summary>
        /// Texture size in bytes.
        /// </summary>
        public ulong Size => (ulong)_sizeInfo.TotalSize;

        /// <summary>
        /// Whether or not the texture belongs is a view.
        /// </summary>
        public bool IsView => _viewStorage != this;

        private int _referenceCount;
        private List<TexturePoolOwner> _poolOwners;

        /// <summary>
        /// Constructs a new instance of the cached GPU texture.
        /// </summary>
        /// <param name="context">GPU context that the texture belongs to</param>
        /// <param name="physicalMemory">Physical memory where the texture is mapped</param>
        /// <param name="info">Texture information</param>
        /// <param name="sizeInfo">Size information of the texture</param>
        /// <param name="range">Physical memory ranges where the texture data is located</param>
        /// <param name="firstLayer">The first layer of the texture, or 0 if the texture has no parent</param>
        /// <param name="firstLevel">The first mipmap level of the texture, or 0 if the texture has no parent</param>
        /// <param name="scaleFactor">The floating point scale factor to initialize with</param>
        /// <param name="scaleMode">The scale mode to initialize with</param>
        private Texture(
            GpuContext context,
            PhysicalMemory physicalMemory,
            TextureInfo info,
            SizeInfo sizeInfo,
            MultiRange range,
            int firstLayer,
            int firstLevel,
            float scaleFactor,
            TextureScaleMode scaleMode)
        {
            InitializeTexture(context, physicalMemory, info, sizeInfo, range);

            FirstLayer = firstLayer;
            FirstLevel = firstLevel;

            ScaleFactor = scaleFactor;
            ScaleMode = scaleMode;

            InitializeData(true);
        }

        /// <summary>
        /// Constructs a new instance of the cached GPU texture.
        /// </summary>
        /// <param name="context">GPU context that the texture belongs to</param>
        /// <param name="physicalMemory">Physical memory where the texture is mapped</param>
        /// <param name="info">Texture information</param>
        /// <param name="sizeInfo">Size information of the texture</param>
        /// <param name="range">Physical memory ranges where the texture data is located</param>
        /// <param name="scaleMode">The scale mode to initialize with. If scaled, the texture's data is loaded immediately and scaled up</param>
        public Texture(
            GpuContext context,
            PhysicalMemory physicalMemory,
            TextureInfo info,
            SizeInfo sizeInfo,
            MultiRange range,
            TextureScaleMode scaleMode)
        {
            ScaleFactor = 1f; // Texture is first loaded at scale 1x.
            ScaleMode = scaleMode;

            InitializeTexture(context, physicalMemory, info, sizeInfo, range);
        }

        /// <summary>
        /// Common texture initialization method.
        /// This sets the context, info and sizeInfo fields.
        /// Other fields are initialized with their default values.
        /// </summary>
        /// <param name="context">GPU context that the texture belongs to</param>
        /// <param name="physicalMemory">Physical memory where the texture is mapped</param>
        /// <param name="info">Texture information</param>
        /// <param name="sizeInfo">Size information of the texture</param>
        /// <param name="range">Physical memory ranges where the texture data is located</param>
        private void InitializeTexture(
            GpuContext context,
            PhysicalMemory physicalMemory,
            TextureInfo info,
            SizeInfo sizeInfo,
            MultiRange range)
        {
            _context = context;
            _physicalMemory = physicalMemory;
            _sizeInfo = sizeInfo;
            Range = range;

            SetInfo(info);

            _viewStorage = this;

            _views = new List<Texture>();
            _poolOwners = new List<TexturePoolOwner>();
        }

        /// <summary>
        /// Initializes the data for a texture. Can optionally initialize the texture with or without data.
        /// If the texture is a view, it will initialize memory tracking to be non-dirty.
        /// </summary>
        /// <param name="isView">True if the texture is a view, false otherwise</param>
        /// <param name="withData">True if the texture is to be initialized with data</param>
        public void InitializeData(bool isView, bool withData = false)
        {
            withData |= Group != null && Group.FlushIncompatibleOverlapsIfNeeded();

            if (withData)
            {
                Debug.Assert(!isView);

                TextureCreateInfo createInfo = TextureCache.GetCreateInfo(Info, _context.Capabilities, ScaleFactor);
                HostTexture = _context.Renderer.CreateTexture(createInfo, ScaleFactor);

                SynchronizeMemory(); // Load the data.
                if (ScaleMode == TextureScaleMode.Scaled)
                {
                    SetScale(GraphicsConfig.ResScale); // Scale the data up.
                }
            }
            else
            {
                _hasData = true;

                if (!isView)
                {
                    // Don't update this texture the next time we synchronize.
                    CheckModified(true);

                    if (ScaleMode == TextureScaleMode.Scaled)
                    {
                        // Don't need to start at 1x as there is no data to scale, just go straight to the target scale.
                        ScaleFactor = GraphicsConfig.ResScale;
                    }

                    TextureCreateInfo createInfo = TextureCache.GetCreateInfo(Info, _context.Capabilities, ScaleFactor);
                    HostTexture = _context.Renderer.CreateTexture(createInfo, ScaleFactor);
                }
            }
        }

        /// <summary>
        /// Initialize a new texture group with this texture as storage.
        /// </summary>
        /// <param name="hasLayerViews">True if the texture will have layer views</param>
        /// <param name="hasMipViews">True if the texture will have mip views</param>
        /// <param name="incompatibleOverlaps">Groups that overlap with this one but are incompatible</param>
        public void InitializeGroup(bool hasLayerViews, bool hasMipViews, List<TextureIncompatibleOverlap> incompatibleOverlaps)
        {
            Group = new TextureGroup(_context, _physicalMemory, this, incompatibleOverlaps);

            Group.Initialize(ref _sizeInfo, hasLayerViews, hasMipViews);
        }

        /// <summary>
        /// Create a texture view from this texture.
        /// A texture view is defined as a child texture, from a sub-range of their parent texture.
        /// For example, the initial layer and mipmap level of the view can be defined, so the texture
        /// will start at the given layer/level of the parent texture.
        /// </summary>
        /// <param name="info">Child texture information</param>
        /// <param name="sizeInfo">Child texture size information</param>
        /// <param name="range">Physical memory ranges where the texture data is located</param>
        /// <param name="firstLayer">Start layer of the child texture on the parent texture</param>
        /// <param name="firstLevel">Start mipmap level of the child texture on the parent texture</param>
        /// <returns>The child texture</returns>
        public Texture CreateView(TextureInfo info, SizeInfo sizeInfo, MultiRange range, int firstLayer, int firstLevel)
        {
            Texture texture = new Texture(
                _context,
                _physicalMemory,
                info,
                sizeInfo,
                range,
                FirstLayer + firstLayer,
                FirstLevel + firstLevel,
                ScaleFactor,
                ScaleMode);

            TextureCreateInfo createInfo = TextureCache.GetCreateInfo(info, _context.Capabilities, ScaleFactor);
            texture.HostTexture = HostTexture.CreateView(createInfo, firstLayer, firstLevel);

            _viewStorage.AddView(texture);

            return texture;
        }

        /// <summary>
        /// Adds a child texture to this texture.
        /// </summary>
        /// <param name="texture">The child texture</param>
        private void AddView(Texture texture)
        {
            IncrementReferenceCount();

            _views.Add(texture);

            texture._viewStorage = this;

            Group.UpdateViews(_views);

            if (texture.Group != null && texture.Group != Group)
            {
                if (texture.Group.Storage == texture)
                {
                    // This texture's group is no longer used.
                    Group.Inherit(texture.Group);

                    texture.Group.Dispose();
                }
            }

            texture.Group = Group;
        }

        /// <summary>
        /// Removes a child texture from this texture.
        /// </summary>
        /// <param name="texture">The child texture</param>
        private void RemoveView(Texture texture)
        {
            _views.Remove(texture);

            texture._viewStorage = texture;

            DecrementReferenceCount();
        }

        /// <summary>
        /// Create a copy dependency to a texture that is view compatible with this one.
        /// When either texture is modified, the texture data will be copied to the other to keep them in sync.
        /// This is essentially an emulated view, useful for handling multiple view parents or format incompatibility.
        /// This also forces a copy on creation, to or from the given texture to get them in sync immediately.
        /// </summary>
        /// <param name="contained">The view compatible texture to create a dependency to</param>
        /// <param name="layer">The base layer of the given texture relative to this one</param>
        /// <param name="level">The base level of the given texture relative to this one</param>
        /// <param name="copyTo">True if this texture is first copied to the given one, false for the opposite direction</param>
        public void CreateCopyDependency(Texture contained, int layer, int level, bool copyTo)
        {
            if (contained.Group == Group)
            {
                return;
            }

            Group.CreateCopyDependency(contained, FirstLayer + layer, FirstLevel + level, copyTo);
        }

        /// <summary>
        /// Changes the texture size.
        /// </summary>
        /// <remarks>
        /// This operation may also change the size of all mipmap levels, including from the parent
        /// and other possible child textures, to ensure that all sizes are consistent.
        /// </remarks>
        /// <param name="width">The new texture width</param>
        /// <param name="height">The new texture height</param>
        /// <param name="depthOrLayers">The new texture depth (for 3D textures) or layers (for layered textures)</param>
        public void ChangeSize(int width, int height, int depthOrLayers)
        {
            int blockWidth = Info.FormatInfo.BlockWidth;
            int blockHeight = Info.FormatInfo.BlockHeight;

            width  <<= FirstLevel;
            height <<= FirstLevel;

            if (Target == Target.Texture3D)
            {
                depthOrLayers <<= FirstLevel;
            }
            else
            {
                depthOrLayers = _viewStorage.Info.DepthOrLayers;
            }

            _viewStorage.RecreateStorageOrView(width, height, blockWidth, blockHeight, depthOrLayers);

            foreach (Texture view in _viewStorage._views)
            {
                int viewWidth  = Math.Max(1, width  >> view.FirstLevel);
                int viewHeight = Math.Max(1, height >> view.FirstLevel);

                int viewDepthOrLayers;

                if (view.Info.Target == Target.Texture3D)
                {
                    viewDepthOrLayers = Math.Max(1, depthOrLayers >> view.FirstLevel);
                }
                else
                {
                    viewDepthOrLayers = view.Info.DepthOrLayers;
                }

                view.RecreateStorageOrView(viewWidth, viewHeight, blockWidth, blockHeight, viewDepthOrLayers);
            }
        }

        /// <summary>
        /// Recreates the texture storage (or view, in the case of child textures) of this texture.
        /// This allows recreating the texture with a new size.
        /// A copy is automatically performed from the old to the new texture.
        /// </summary>
        /// <param name="width">The new texture width</param>
        /// <param name="height">The new texture height</param>
        /// <param name="width">The block width related to the given width</param>
        /// <param name="height">The block height related to the given height</param>
        /// <param name="depthOrLayers">The new texture depth (for 3D textures) or layers (for layered textures)</param>
        private void RecreateStorageOrView(int width, int height, int blockWidth, int blockHeight, int depthOrLayers)
        {
            RecreateStorageOrView(
                BitUtils.DivRoundUp(width * Info.FormatInfo.BlockWidth, blockWidth),
                BitUtils.DivRoundUp(height * Info.FormatInfo.BlockHeight, blockHeight),
                depthOrLayers);
        }

        /// <summary>
        /// Recreates the texture storage (or view, in the case of child textures) of this texture.
        /// This allows recreating the texture with a new size.
        /// A copy is automatically performed from the old to the new texture.
        /// </summary>
        /// <param name="width">The new texture width</param>
        /// <param name="height">The new texture height</param>
        /// <param name="depthOrLayers">The new texture depth (for 3D textures) or layers (for layered textures)</param>
        private void RecreateStorageOrView(int width, int height, int depthOrLayers)
        {
            ChangedSize = true;

            SetInfo(new TextureInfo(
                Info.GpuAddress,
                width,
                height,
                depthOrLayers,
                Info.Levels,
                Info.SamplesInX,
                Info.SamplesInY,
                Info.Stride,
                Info.IsLinear,
                Info.GobBlocksInY,
                Info.GobBlocksInZ,
                Info.GobBlocksInTileX,
                Info.Target,
                Info.FormatInfo,
                Info.DepthStencilMode,
                Info.SwizzleR,
                Info.SwizzleG,
                Info.SwizzleB,
                Info.SwizzleA));

            TextureCreateInfo createInfo = TextureCache.GetCreateInfo(Info, _context.Capabilities, ScaleFactor);

            if (_viewStorage != this)
            {
                ReplaceStorage(_viewStorage.HostTexture.CreateView(createInfo, FirstLayer, FirstLevel));
            }
            else
            {
                ITexture newStorage = _context.Renderer.CreateTexture(createInfo, ScaleFactor);

                HostTexture.CopyTo(newStorage, 0, 0);

                ReplaceStorage(newStorage);
            }
        }

        /// <summary>
        /// Blacklists this texture from being scaled. Resets its scale to 1 if needed.
        /// </summary>
        public void BlacklistScale()
        {
            ScaleMode = TextureScaleMode.Blacklisted;
            SetScale(1f);
        }

        /// <summary>
        /// Propagates the scale between this texture and another to ensure they have the same scale.
        /// If one texture is blacklisted from scaling, the other will become blacklisted too.
        /// </summary>
        /// <param name="other">The other texture</param>
        public void PropagateScale(Texture other)
        {
            if (other.ScaleMode == TextureScaleMode.Blacklisted || ScaleMode == TextureScaleMode.Blacklisted)
            {
                BlacklistScale();
                other.BlacklistScale();
            }
            else
            {
                // Prefer the configured scale if present. If not, prefer the max.
                float targetScale = GraphicsConfig.ResScale;
                float sharedScale = (ScaleFactor == targetScale || other.ScaleFactor == targetScale) ? targetScale : Math.Max(ScaleFactor, other.ScaleFactor);

                SetScale(sharedScale);
                other.SetScale(sharedScale);
            }
        }

        /// <summary>
        /// Copy the host texture to a scaled one. If a texture is not provided, create it with the given scale.
        /// </summary>
        /// <param name="scale">Scale factor</param>
        /// <param name="storage">Texture to use instead of creating one</param>
        /// <returns>A host texture containing a scaled version of this texture</returns>
        private ITexture GetScaledHostTexture(float scale, ITexture storage = null)
        {
            if (storage == null)
            {
                TextureCreateInfo createInfo = TextureCache.GetCreateInfo(Info, _context.Capabilities, scale);
                storage = _context.Renderer.CreateTexture(createInfo, scale);
            }

            HostTexture.CopyTo(storage, new Extents2D(0, 0, HostTexture.Width, HostTexture.Height), new Extents2D(0, 0, storage.Width, storage.Height), true);

            return storage;
        }

        /// <summary>
        /// Sets the Scale Factor on this texture, and immediately recreates it at the correct size.
        /// When a texture is resized, a scaled copy is performed from the old texture to the new one, to ensure no data is lost.
        /// If scale is equivalent, this only propagates the blacklisted/scaled mode.
        /// If called on a view, its storage is resized instead.
        /// When resizing storage, all texture views are recreated.
        /// </summary>
        /// <param name="scale">The new scale factor for this texture</param>
        public void SetScale(float scale)
        {
            bool unscaled = ScaleMode == TextureScaleMode.Blacklisted || (ScaleMode == TextureScaleMode.Undesired && scale == 1);
            TextureScaleMode newScaleMode = unscaled ? ScaleMode : TextureScaleMode.Scaled;

            if (_viewStorage != this)
            {
                _viewStorage.ScaleMode = newScaleMode;
                _viewStorage.SetScale(scale);
                return;
            }

            if (ScaleFactor != scale)
            {
                Logger.Debug?.Print(LogClass.Gpu, $"Rescaling {Info.Width}x{Info.Height} {Info.FormatInfo.Format.ToString()} to ({ScaleFactor} to {scale}). ");

                ScaleFactor = scale;

                ITexture newStorage = GetScaledHostTexture(ScaleFactor);

                Logger.Debug?.Print(LogClass.Gpu, $"  Copy performed: {HostTexture.Width}x{HostTexture.Height} to {newStorage.Width}x{newStorage.Height}");

                ReplaceStorage(newStorage);

                // All views must be recreated against the new storage.

                foreach (var view in _views)
                {
                    Logger.Debug?.Print(LogClass.Gpu, $"  Recreating view {Info.Width}x{Info.Height} {Info.FormatInfo.Format.ToString()}.");
                    view.ScaleFactor = scale;

                    TextureCreateInfo viewCreateInfo = TextureCache.GetCreateInfo(view.Info, _context.Capabilities, scale);
                    ITexture newView = HostTexture.CreateView(viewCreateInfo, view.FirstLayer - FirstLayer, view.FirstLevel - FirstLevel);

                    view.ReplaceStorage(newView);
                    view.ScaleMode = newScaleMode;
                }
            }

            if (ScaleMode != newScaleMode)
            {
                ScaleMode = newScaleMode;

                foreach (var view in _views)
                {
                    view.ScaleMode = newScaleMode;
                }
            }
        }

        /// <summary>
        /// Checks if the memory for this texture was modified, and returns true if it was.
        /// The modified flags are optionally consumed as a result.
        /// </summary>
        /// <param name="consume">True to consume the dirty flags and reprotect, false to leave them as is</param>
        /// <returns>True if the texture was modified, false otherwise.</returns>
        public bool CheckModified(bool consume)
        {
            return Group.CheckDirty(this, consume);
        }

        /// <summary>
        /// Synchronizes guest and host memory.
        /// This will overwrite the texture data with the texture data on the guest memory, if a CPU
        /// modification is detected.
        /// Be aware that this can cause texture data written by the GPU to be lost, this is just a
        /// one way copy (from CPU owned to GPU owned memory).
        /// </summary>
        public void SynchronizeMemory()
        {
            if (Target == Target.TextureBuffer)
            {
                return;
            }

            if (!_dirty)
            {
                return;
            }

            _dirty = false;

            if (_hasData)
            {
                Group.SynchronizeMemory(this);
            }
            else
            {
                Group.CheckDirty(this, true);
                SynchronizeFull();
            }
        }

        /// <summary>
        /// Signal that this texture is dirty, indicating that the texture group must be checked.
        /// </summary>
        public void SignalGroupDirty()
        {
            _dirty = true;
        }

        /// <summary>
        /// Signal that the modified state is dirty, indicating that the texture group should be notified when it changes.
        /// </summary>
        public void SignalModifiedDirty()
        {
            _modifiedStale = true;
        }

        /// <summary>
        /// Fully synchronizes guest and host memory.
        /// This will replace the entire texture with the data present in guest memory.
        /// </summary>
        public void SynchronizeFull()
        {
            if (_hasData)
            {
                BlacklistScale();
            }

            ReadOnlySpan<byte> data = _physicalMemory.GetSpan(Range);

            // If the host does not support ASTC compression, we need to do the decompression.
            // The decompression is slow, so we want to avoid it as much as possible.
            // This does a byte-by-byte check and skips the update if the data is equal in this case.
            // This improves the speed on applications that overwrites ASTC data without changing anything.
            if (Info.FormatInfo.Format.IsAstc() && !_context.Capabilities.SupportsAstcCompression)
            {
                if (_updateCount < ByteComparisonSwitchThreshold)
                {
                    _updateCount++;
                }
                else
                {
                    bool dataMatches = _currentData != null && data.SequenceEqual(_currentData);
                    _currentData = data.ToArray();
                    if (dataMatches)
                    {
                        return;
                    }
                }
            }

            data = ConvertToHostCompatibleFormat(data);

            HostTexture.SetData(data);

            _hasData = true;
        }

        /// <summary>
        /// Uploads new texture data to the host GPU.
        /// </summary>
        /// <param name="data">New data</param>
        public void SetData(ReadOnlySpan<byte> data)
        {
            BlacklistScale();

            Group.CheckDirty(this, true);

            AlwaysFlushOnOverlap = true;

            HostTexture.SetData(data);

            _hasData = true;
        }

        /// <summary>
        /// Uploads new texture data to the host GPU for a specific layer/level.
        /// </summary>
        /// <param name="data">New data</param>
        /// <param name="layer">Target layer</param>
        /// <param name="level">Target level</param>
        public void SetData(ReadOnlySpan<byte> data, int layer, int level)
        {
            BlacklistScale();

            HostTexture.SetData(data, layer, level);

            _currentData = null;

            _hasData = true;
        }

        /// <summary>
        /// Converts texture data to a format and layout that is supported by the host GPU.
        /// </summary>
        /// <param name="data">Data to be converted</param>
        /// <param name="level">Mip level to convert</param>
        /// <param name="single">True to convert a single slice</param>
        /// <returns>Converted data</returns>
        public ReadOnlySpan<byte> ConvertToHostCompatibleFormat(ReadOnlySpan<byte> data, int level = 0, bool single = false)
        {
            int width = Info.Width;
            int height = Info.Height;

            int depth = _depth;
            int layers = single ? 1 : _layers;
            int levels = single ? 1 : (Info.Levels - level);

            width = Math.Max(width >> level, 1);
            height = Math.Max(height >> level, 1);
            depth = Math.Max(depth >> level, 1);

            if (Info.IsLinear)
            {
                data = LayoutConverter.ConvertLinearStridedToLinear(
                    width,
                    height,
                    Info.FormatInfo.BlockWidth,
                    Info.FormatInfo.BlockHeight,
                    Info.Stride,
                    Info.Stride,
                    Info.FormatInfo.BytesPerPixel,
                    data);
            }
            else
            {
                data = LayoutConverter.ConvertBlockLinearToLinear(
                    width,
                    height,
                    depth,
                    single ? 1 : depth,
                    levels,
                    layers,
                    Info.FormatInfo.BlockWidth,
                    Info.FormatInfo.BlockHeight,
                    Info.FormatInfo.BytesPerPixel,
                    Info.GobBlocksInY,
                    Info.GobBlocksInZ,
                    Info.GobBlocksInTileX,
                    _sizeInfo,
                    data);
            }

            // Handle compressed cases not supported by the host:
            // - ASTC is usually not supported on desktop cards.
            // - BC4/BC5 is not supported on 3D textures.
            if (!_context.Capabilities.SupportsAstcCompression && Format.IsAstc())
            {
                if (!AstcDecoder.TryDecodeToRgba8P(
                    data.ToArray(),
                    Info.FormatInfo.BlockWidth,
                    Info.FormatInfo.BlockHeight,
                    width,
                    height,
                    depth,
                    levels,
                    layers,
                    out byte[] decoded))
                {
                    string texInfo = $"{Info.Target} {Info.FormatInfo.Format} {Info.Width}x{Info.Height}x{Info.DepthOrLayers} levels {Info.Levels}";

                    Logger.Debug?.Print(LogClass.Gpu, $"Invalid ASTC texture at 0x{Info.GpuAddress:X} ({texInfo}).");
                }

                if (GraphicsConfig.EnableTextureRecompression)
                {
                    decoded = BCnEncoder.EncodeBC7(decoded, width, height, depth, levels, layers);
                }

                data = decoded;
            }
            else if (!_context.Capabilities.SupportsR4G4Format && Format == Format.R4G4Unorm)
            {
                data = PixelConverter.ConvertR4G4ToR4G4B4A4(data);
            }
            else if (!TextureCompatibility.HostSupportsBcFormat(Format, Target, _context.Capabilities))
            {
                switch (Format)
                {
                    case Format.Bc1RgbaSrgb:
                    case Format.Bc1RgbaUnorm:
                        data = BCnDecoder.DecodeBC1(data, width, height, depth, levels, layers);
                        break;
                    case Format.Bc2Srgb:
                    case Format.Bc2Unorm:
                        data = BCnDecoder.DecodeBC2(data, width, height, depth, levels, layers);
                        break;
                    case Format.Bc3Srgb:
                    case Format.Bc3Unorm:
                        data = BCnDecoder.DecodeBC3(data, width, height, depth, levels, layers);
                        break;
                    case Format.Bc4Snorm:
                    case Format.Bc4Unorm:
                        data = BCnDecoder.DecodeBC4(data, width, height, depth, levels, layers, Format == Format.Bc4Snorm);
                        break;
                    case Format.Bc5Snorm:
                    case Format.Bc5Unorm:
                        data = BCnDecoder.DecodeBC5(data, width, height, depth, levels, layers, Format == Format.Bc5Snorm);
                        break;
                    case Format.Bc6HSfloat:
                    case Format.Bc6HUfloat:
                        data = BCnDecoder.DecodeBC6(data, width, height, depth, levels, layers, Format == Format.Bc6HSfloat);
                        break;
                    case Format.Bc7Srgb:
                    case Format.Bc7Unorm:
                        data = BCnDecoder.DecodeBC7(data, width, height, depth, levels, layers);
                        break;
                }
            }

            return data;
        }

        /// <summary>
        /// Converts texture data from a format and layout that is supported by the host GPU, back into the intended format on the guest GPU.
        /// </summary>
        /// <param name="output">Optional output span to convert into</param>
        /// <param name="data">Data to be converted</param>
        /// <param name="level">Mip level to convert</param>
        /// <param name="single">True to convert a single slice</param>
        /// <returns>Converted data</returns>
        public ReadOnlySpan<byte> ConvertFromHostCompatibleFormat(Span<byte> output, ReadOnlySpan<byte> data, int level = 0, bool single = false)
        {
            if (Target != Target.TextureBuffer)
            {
                int width = Info.Width;
                int height = Info.Height;

                int depth = _depth;
                int layers = single ? 1 : _layers;
                int levels = single ? 1 : (Info.Levels - level);

                width = Math.Max(width >> level, 1);
                height = Math.Max(height >> level, 1);
                depth = Math.Max(depth >> level, 1);

                if (Info.IsLinear)
                {
                    data = LayoutConverter.ConvertLinearToLinearStrided(
                        output,
                        Info.Width,
                        Info.Height,
                        Info.FormatInfo.BlockWidth,
                        Info.FormatInfo.BlockHeight,
                        Info.Stride,
                        Info.FormatInfo.BytesPerPixel,
                        data);
                }
                else
                {
                    data = LayoutConverter.ConvertLinearToBlockLinear(
                        output,
                        width,
                        height,
                        depth,
                        single ? 1 : depth,
                        levels,
                        layers,
                        Info.FormatInfo.BlockWidth,
                        Info.FormatInfo.BlockHeight,
                        Info.FormatInfo.BytesPerPixel,
                        Info.GobBlocksInY,
                        Info.GobBlocksInZ,
                        Info.GobBlocksInTileX,
                        _sizeInfo,
                        data);
                }
            }

            return data;
        }

        /// <summary>
        /// Flushes the texture data.
        /// This causes the texture data to be written back to guest memory.
        /// If the texture was written by the GPU, this includes all modification made by the GPU
        /// up to this point.
        /// Be aware that this is an expensive operation, avoid calling it unless strictly needed.
        /// This may cause data corruption if the memory is already being used for something else on the CPU side.
        /// </summary>
        /// <param name="tracked">Whether or not the flush triggers write tracking. If it doesn't, the texture will not be blacklisted for scaling either.</param>
        /// <returns>True if data was flushed, false otherwise</returns>
        public bool FlushModified(bool tracked = true)
        {
            return TextureCompatibility.CanTextureFlush(Info, _context.Capabilities) && Group.FlushModified(this, tracked);
        }

        /// <summary>
        /// Flushes the texture data.
        /// This causes the texture data to be written back to guest memory.
        /// If the texture was written by the GPU, this includes all modification made by the GPU
        /// up to this point.
        /// Be aware that this is an expensive operation, avoid calling it unless strictly needed.
        /// This may cause data corruption if the memory is already being used for something else on the CPU side.
        /// </summary>
        /// <param name="tracked">Whether or not the flush triggers write tracking. If it doesn't, the texture will not be blacklisted for scaling either.</param>
        public void Flush(bool tracked)
        {
            if (TextureCompatibility.CanTextureFlush(Info, _context.Capabilities))
            {
                FlushTextureDataToGuest(tracked);
            }
        }

        /// <summary>
        /// Gets a host texture to use for flushing the texture, at 1x resolution.
        /// If the HostTexture is already at 1x resolution, it is returned directly.
        /// </summary>
        /// <returns>The host texture to flush</returns>
        public ITexture GetFlushTexture()
        {
            ITexture texture = HostTexture;
            if (ScaleFactor != 1f)
            {
                // If needed, create a texture to flush back to host at 1x scale.
                texture = _flushHostTexture = GetScaledHostTexture(1f, _flushHostTexture);
            }

            return texture;
        }

        /// <summary>
        /// Gets data from the host GPU, and flushes it all to guest memory.
        /// </summary>
        /// <remarks>
        /// This method should be used to retrieve data that was modified by the host GPU.
        /// This is not cheap, avoid doing that unless strictly needed.
        /// When possible, the data is written directly into guest memory, rather than copied.
        /// </remarks>
        /// <param name="tracked">True if writing the texture data is tracked, false otherwise</param>
        /// <param name="texture">The specific host texture to flush. Defaults to this texture</param>
        public void FlushTextureDataToGuest(bool tracked, ITexture texture = null)
        {
            using WritableRegion region = _physicalMemory.GetWritableRegion(Range, tracked);

            GetTextureDataFromGpu(region.Memory.Span, tracked, texture);
        }

        /// <summary>
        /// Gets data from the host GPU.
        /// </summary>
        /// <remarks>
        /// This method should be used to retrieve data that was modified by the host GPU.
        /// This is not cheap, avoid doing that unless strictly needed.
        /// </remarks>
        /// <param name="output">An output span to place the texture data into. If empty, one is generated</param>
        /// <param name="blacklist">True if the texture should be blacklisted, false otherwise</param>
        /// <param name="texture">The specific host texture to flush. Defaults to this texture</param>
        /// <returns>The span containing the texture data</returns>
        private ReadOnlySpan<byte> GetTextureDataFromGpu(Span<byte> output, bool blacklist, ITexture texture = null)
        {
            ReadOnlySpan<byte> data;

            if (texture != null)
            {
                data = texture.GetData();
            }
            else
            {
                if (blacklist)
                {
                    BlacklistScale();
                    data = HostTexture.GetData();
                }
                else if (ScaleFactor != 1f)
                {
                    float scale = ScaleFactor;
                    SetScale(1f);
                    data = HostTexture.GetData();
                    SetScale(scale);
                }
                else
                {
                    data = HostTexture.GetData();
                }
            }

            data = ConvertFromHostCompatibleFormat(output, data);

            return data;
        }

        /// <summary>
        /// Gets data from the host GPU for a single slice.
        /// </summary>
        /// <remarks>
        /// This method should be used to retrieve data that was modified by the host GPU.
        /// This is not cheap, avoid doing that unless strictly needed.
        /// </remarks>
        /// <param name="output">An output span to place the texture data into. If empty, one is generated</param>
        /// <param name="layer">The layer of the texture to flush</param>
        /// <param name="level">The level of the texture to flush</param>
        /// <param name="blacklist">True if the texture should be blacklisted, false otherwise</param>
        /// <param name="texture">The specific host texture to flush. Defaults to this texture</param>
        /// <returns>The span containing the texture data</returns>
        public ReadOnlySpan<byte> GetTextureDataSliceFromGpu(Span<byte> output, int layer, int level, bool blacklist, ITexture texture = null)
        {
            ReadOnlySpan<byte> data;

            if (texture != null)
            {
                data = texture.GetData(layer, level);
            }
            else
            {
                if (blacklist)
                {
                    BlacklistScale();
                    data = HostTexture.GetData(layer, level);
                }
                else if (ScaleFactor != 1f)
                {
                    float scale = ScaleFactor;
                    SetScale(1f);
                    data = HostTexture.GetData(layer, level);
                    SetScale(scale);
                }
                else
                {
                    data = HostTexture.GetData(layer, level);
                }
            }

            data = ConvertFromHostCompatibleFormat(output, data, level, true);

            return data;
        }

        /// <summary>
        /// This performs a strict comparison, used to check if this texture is equal to the one supplied.
        /// </summary>
        /// <param name="info">Texture information to compare against</param>
        /// <param name="flags">Comparison flags</param>
        /// <returns>A value indicating how well this texture matches the given info</returns>
        public TextureMatchQuality IsExactMatch(TextureInfo info, TextureSearchFlags flags)
        {
            TextureMatchQuality matchQuality = TextureCompatibility.FormatMatches(Info, info, (flags & TextureSearchFlags.ForSampler) != 0, (flags & TextureSearchFlags.ForCopy) != 0);

            if (matchQuality == TextureMatchQuality.NoMatch)
            {
                return matchQuality;
            }

            if (!TextureCompatibility.LayoutMatches(Info, info))
            {
                return TextureMatchQuality.NoMatch;
            }

            if (!TextureCompatibility.SizeMatches(Info, info, (flags & TextureSearchFlags.Strict) == 0, FirstLevel))
            {
                return TextureMatchQuality.NoMatch;
            }

            if ((flags & TextureSearchFlags.ForSampler) != 0 || (flags & TextureSearchFlags.Strict) != 0)
            {
                if (!TextureCompatibility.SamplerParamsMatches(Info, info))
                {
                    return TextureMatchQuality.NoMatch;
                }
            }

            if ((flags & TextureSearchFlags.ForCopy) != 0)
            {
                bool msTargetCompatible = Info.Target == Target.Texture2DMultisample && info.Target == Target.Texture2D;

                if (!msTargetCompatible && !TextureCompatibility.TargetAndSamplesCompatible(Info, info))
                {
                    return TextureMatchQuality.NoMatch;
                }
            }
            else if (!TextureCompatibility.TargetAndSamplesCompatible(Info, info))
            {
                return TextureMatchQuality.NoMatch;
            }

            return Info.Levels == info.Levels ? matchQuality : TextureMatchQuality.NoMatch;
        }

        /// <summary>
        /// Check if it's possible to create a view, with the given parameters, from this texture.
        /// </summary>
        /// <param name="info">Texture view information</param>
        /// <param name="range">Texture view physical memory ranges</param>
        /// <param name="layerSize">Layer size on the given texture</param>
        /// <param name="caps">Host GPU capabilities</param>
        /// <param name="firstLayer">Texture view initial layer on this texture</param>
        /// <param name="firstLevel">Texture view first mipmap level on this texture</param>
        /// <returns>The level of compatiblilty a view with the given parameters created from this texture has</returns>
        public TextureViewCompatibility IsViewCompatible(TextureInfo info, MultiRange range, int layerSize, Capabilities caps, out int firstLayer, out int firstLevel)
        {
            TextureViewCompatibility result = TextureViewCompatibility.Full;

            result = TextureCompatibility.PropagateViewCompatibility(result, TextureCompatibility.ViewFormatCompatible(Info, info, caps));
            if (result != TextureViewCompatibility.Incompatible)
            {
                result = TextureCompatibility.PropagateViewCompatibility(result, TextureCompatibility.ViewTargetCompatible(Info, info, ref caps));

                bool bothMs = Info.Target.IsMultisample() && info.Target.IsMultisample();
                if (bothMs && (Info.SamplesInX != info.SamplesInX || Info.SamplesInY != info.SamplesInY))
                {
                    result = TextureViewCompatibility.Incompatible;
                }

                if (result == TextureViewCompatibility.Full && Info.FormatInfo.Format != info.FormatInfo.Format && !_context.Capabilities.SupportsMismatchingViewFormat)
                {
                    // AMD and Intel have a bug where the view format is always ignored;
                    // they use the parent format instead.
                    // Create a copy dependency to avoid this issue.

                    result = TextureViewCompatibility.CopyOnly;
                }
            }

            firstLayer = 0;
            firstLevel = 0;

            if (result == TextureViewCompatibility.Incompatible)
            {
                return TextureViewCompatibility.Incompatible;
            }

            int offset = Range.FindOffset(range);

            if (offset < 0 || !_sizeInfo.FindView(offset, out firstLayer, out firstLevel))
            {
                return TextureViewCompatibility.LayoutIncompatible;
            }

            if (!TextureCompatibility.ViewLayoutCompatible(Info, info, firstLevel))
            {
                return TextureViewCompatibility.LayoutIncompatible;
            }

            if (info.GetSlices() > 1 && LayerSize != layerSize)
            {
                return TextureViewCompatibility.LayoutIncompatible;
            }

            result = TextureCompatibility.PropagateViewCompatibility(result, TextureCompatibility.ViewSizeMatches(Info, info, firstLevel));
            result = TextureCompatibility.PropagateViewCompatibility(result, TextureCompatibility.ViewSubImagesInBounds(Info, info, firstLayer, firstLevel));

            return result;
        }

        /// <summary>
        /// Gets a texture of the specified target type from this texture.
        /// This can be used to get an array texture from a non-array texture and vice-versa.
        /// If this texture and the requested targets are equal, then this texture Host texture is returned directly.
        /// </summary>
        /// <param name="target">The desired target type</param>
        /// <returns>A view of this texture with the requested target, or null if the target is invalid for this texture</returns>
        public ITexture GetTargetTexture(Target target)
        {
            if (target == Target)
            {
                return HostTexture;
            }

            if (_arrayViewTexture == null && IsSameDimensionsTarget(target))
            {
                FormatInfo formatInfo = TextureCompatibility.ToHostCompatibleFormat(Info, _context.Capabilities);

                TextureCreateInfo createInfo = new TextureCreateInfo(
                    Info.Width,
                    Info.Height,
                    target == Target.CubemapArray ? 6 : 1,
                    Info.Levels,
                    Info.Samples,
                    formatInfo.BlockWidth,
                    formatInfo.BlockHeight,
                    formatInfo.BytesPerPixel,
                    formatInfo.Format,
                    Info.DepthStencilMode,
                    target,
                    Info.SwizzleR,
                    Info.SwizzleG,
                    Info.SwizzleB,
                    Info.SwizzleA);

                ITexture viewTexture = HostTexture.CreateView(createInfo, 0, 0);

                _arrayViewTexture = viewTexture;
                _arrayViewTarget  = target;

                return viewTexture;
            }
            else if (_arrayViewTarget == target)
            {
                return _arrayViewTexture;
            }

            return null;
        }

        /// <summary>
        /// Determine if this texture can have anisotropic filtering forced.
        /// Filtered textures that we might want to force anisotropy on should have a lot of mip levels.
        /// </summary>
        /// <returns>True if anisotropic filtering can be forced, false otherwise</returns>
        private bool CanTextureForceAnisotropy()
        {
            if (!(Target == Target.Texture2D || Target == Target.Texture2DArray))
            {
                return false;
            }

            int maxSize = Math.Max(Info.Width, Info.Height);
            int maxLevels = BitOperations.Log2((uint)maxSize) + 1;

            return Info.Levels >= Math.Min(MinLevelsForForceAnisotropy, maxLevels);
        }

        /// <summary>
        /// Check if this texture and the specified target have the same number of dimensions.
        /// For the purposes of this comparison, 2D and 2D Multisample textures are not considered to have
        /// the same number of dimensions. Same for Cubemap and 3D textures.
        /// </summary>
        /// <param name="target">The target to compare with</param>
        /// <returns>True if both targets have the same number of dimensions, false otherwise</returns>
        private bool IsSameDimensionsTarget(Target target)
        {
            switch (Info.Target)
            {
                case Target.Texture1D:
                case Target.Texture1DArray:
                    return target == Target.Texture1D ||
                           target == Target.Texture1DArray;

                case Target.Texture2D:
                case Target.Texture2DArray:
                    return target == Target.Texture2D ||
                           target == Target.Texture2DArray;

                case Target.Cubemap:
                case Target.CubemapArray:
                    return target == Target.Cubemap ||
                           target == Target.CubemapArray;

                case Target.Texture2DMultisample:
                case Target.Texture2DMultisampleArray:
                    return target == Target.Texture2DMultisample ||
                           target == Target.Texture2DMultisampleArray;

                case Target.Texture3D:
                    return target == Target.Texture3D;
            }

            return false;
        }

        /// <summary>
        /// Replaces view texture information.
        /// This should only be used for child textures with a parent.
        /// </summary>
        /// <param name="parent">The parent texture</param>
        /// <param name="info">The new view texture information</param>
        /// <param name="hostTexture">The new host texture</param>
        /// <param name="firstLayer">The first layer of the view</param>
        /// <param name="firstLevel">The first level of the view</param>
        public void ReplaceView(Texture parent, TextureInfo info, ITexture hostTexture, int firstLayer, int firstLevel)
        {
            IncrementReferenceCount();
            parent._viewStorage.SynchronizeMemory();

            // If this texture has views, they must be given to the new parent.
            if (_views.Count > 0)
            {
                Texture[] viewCopy = _views.ToArray();

                foreach (Texture view in viewCopy)
                {
                    TextureCreateInfo createInfo = TextureCache.GetCreateInfo(view.Info, _context.Capabilities, ScaleFactor);

                    ITexture newView = parent.HostTexture.CreateView(createInfo, view.FirstLayer + firstLayer, view.FirstLevel + firstLevel);

                    view.ReplaceView(parent, view.Info, newView, view.FirstLayer + firstLayer, view.FirstLevel + firstLevel);
                }
            }

            ReplaceStorage(hostTexture);

            if (_viewStorage != this)
            {
                _viewStorage.RemoveView(this);
            }

            FirstLayer = parent.FirstLayer + firstLayer;
            FirstLevel = parent.FirstLevel + firstLevel;
            parent._viewStorage.AddView(this);

            SetInfo(info);
            DecrementReferenceCount();
        }

        /// <summary>
        /// Sets the internal texture information structure.
        /// </summary>
        /// <param name="info">The new texture information</param>
        private void SetInfo(TextureInfo info)
        {
            Info = info;
            Target = info.Target;
            Width = info.Width;
            Height = info.Height;
            CanForceAnisotropy = CanTextureForceAnisotropy();

            _depth  = info.GetDepth();
            _layers = info.GetLayers();
        }

        /// <summary>
        /// Signals that the texture has been modified.
        /// </summary>
        public void SignalModified()
        {
            if (_modifiedStale || Group.HasCopyDependencies)
            {
                _modifiedStale = false;
                Group.SignalModified(this);
            }

            _physicalMemory.TextureCache.Lift(this);
        }

        /// <summary>
        /// Signals that a texture has been bound, or has been unbound.
        /// During this time, lazy copies will not clear the dirty flag.
        /// </summary>
        /// <param name="bound">True if the texture has been bound, false if it has been unbound</param>
        public void SignalModifying(bool bound)
        {
            if (_modifiedStale || Group.HasCopyDependencies)
            {
                _modifiedStale = false;
                Group.SignalModifying(this, bound);
            }

            _physicalMemory.TextureCache.Lift(this);

            if (bound)
            {
                IncrementReferenceCount();
            }
            else
            {
                DecrementReferenceCount();
            }
        }

        /// <summary>
        /// Replaces the host texture, while disposing of the old one if needed.
        /// </summary>
        /// <param name="hostTexture">The new host texture</param>
        private void ReplaceStorage(ITexture hostTexture)
        {
            DisposeTextures();

            HostTexture = hostTexture;
            InvalidatedSequence++;
        }

        /// <summary>
        /// Determine if any of this texture's data overlaps with another.
        /// </summary>
        /// <param name="texture">The texture to check against</param>
        /// <param name="compatibility">The view compatibility of the two textures</param>
        /// <returns>True if any slice of the textures overlap, false otherwise</returns>
        public bool DataOverlaps(Texture texture, TextureViewCompatibility compatibility)
        {
            if (compatibility == TextureViewCompatibility.LayoutIncompatible && Info.GobBlocksInZ > 1 && Info.GobBlocksInZ == texture.Info.GobBlocksInZ)
            {
                // Allow overlapping slices of layout compatible 3D textures with matching GobBlocksInZ, as they are interleaved.
                return false;
            }

            if (texture._sizeInfo.AllOffsets.Length == 1 && _sizeInfo.AllOffsets.Length == 1)
            {
                return Range.OverlapsWith(texture.Range);
            }

            MultiRange otherRange = texture.Range;

            IEnumerable<MultiRange> regions = _sizeInfo.AllRegions().Select((region) => Range.GetSlice((ulong)region.Offset, (ulong)region.Size));
            IEnumerable<MultiRange> otherRegions = texture._sizeInfo.AllRegions().Select((region) => otherRange.GetSlice((ulong)region.Offset, (ulong)region.Size));

            foreach (MultiRange region in regions)
            {
                foreach (MultiRange otherRegion in otherRegions)
                {
                    if (region.OverlapsWith(otherRegion))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Increments the texture reference count.
        /// </summary>
        public void IncrementReferenceCount()
        {
            _referenceCount++;
        }

        /// <summary>
        /// Increments the reference count and records the given texture pool and ID as a pool owner.
        /// </summary>
        /// <param name="pool">The texture pool this texture has been added to</param>
        /// <param name="id">The ID of the reference to this texture in the pool</param>
        public void IncrementReferenceCount(TexturePool pool, int id)
        {
            lock (_poolOwners)
            {
                _poolOwners.Add(new TexturePoolOwner { Pool = pool, ID = id });
            }
            _referenceCount++;
        }

        /// <summary>
        /// Decrements the texture reference count.
        /// When the reference count hits zero, the texture may be deleted and can't be used anymore.
        /// </summary>
        /// <returns>True if the texture is now referenceless, false otherwise</returns>
        public bool DecrementReferenceCount()
        {
            int newRefCount = --_referenceCount;

            if (newRefCount == 0)
            {
                if (_viewStorage != this)
                {
                    _viewStorage.RemoveView(this);
                }

                _physicalMemory.TextureCache.RemoveTextureFromCache(this);
            }

            Debug.Assert(newRefCount >= 0);

            DeleteIfNotUsed();

            return newRefCount <= 0;
        }

        /// <summary>
        /// Decrements the texture reference count, also removing an associated pool owner reference.
        /// When the reference count hits zero, the texture may be deleted and can't be used anymore.
        /// </summary>
        /// <param name="pool">The texture pool this texture is being removed from</param>
        /// <param name="id">The ID of the reference to this texture in the pool</param>
        /// <returns>True if the texture is now referenceless, false otherwise</returns>
        public bool DecrementReferenceCount(TexturePool pool, int id = -1)
        {
            lock (_poolOwners)
            {
                int references = _poolOwners.RemoveAll(entry => entry.Pool == pool && entry.ID == id || id == -1);

                if (references == 0)
                {
                    // This reference has already been removed.
                    return _referenceCount <= 0;
                }

                Debug.Assert(references == 1);
            }

            return DecrementReferenceCount();
        }

        /// <summary>
        /// Forcibly remove this texture from all pools that reference it.
        /// </summary>
        /// <param name="deferred">Indicates if the removal is being done from another thread.</param>
        public void RemoveFromPools(bool deferred)
        {
            lock (_poolOwners)
            {
                foreach (var owner in _poolOwners)
                {
                    owner.Pool.ForceRemove(this, owner.ID, deferred);
                }

                _poolOwners.Clear();
            }

            InvalidatedSequence++;
        }

        /// <summary>
        /// Delete the texture if it is not used anymore.
        /// The texture is considered unused when the reference count is zero,
        /// and it has no child views.
        /// </summary>
        private void DeleteIfNotUsed()
        {
            // We can delete the texture as long it is not being used
            // in any cache (the reference count is 0 in this case), and
            // also all views that may be created from this texture were
            // already deleted (views count is 0).
            if (_referenceCount == 0 && _views.Count == 0)
            {
                Dispose();
            }
        }

        /// <summary>
        /// Performs texture disposal, deleting the texture.
        /// </summary>
        private void DisposeTextures()
        {
            _currentData = null;
            HostTexture.Release();

            _arrayViewTexture?.Release();
            _arrayViewTexture = null;

            _flushHostTexture?.Release();
            _flushHostTexture = null;
        }

        /// <summary>
        /// Called when the memory for this texture has been unmapped.
        /// Calls are from non-gpu threads.
        /// </summary>
        /// <param name="unmapRange">The range of memory being unmapped</param>
        public void Unmapped(MultiRange unmapRange)
        {
            ChangedMapping = true;

            if (Group.Storage == this)
            {
                Group.ClearModified(unmapRange);
            }

            RemoveFromPools(true);
        }

        /// <summary>
        /// Performs texture disposal, deleting the texture.
        /// </summary>
        public void Dispose()
        {
            DisposeTextures();

            Disposed?.Invoke(this);

            if (Group.Storage == this)
            {
                Group.Dispose();
            }
        }

        public override string ToString()
        {
            return string.Join(" ", 
                $"{Width}x{Height}", 
                $"{Target}", 
                $"<{Format}>",
                $"Range={Range}",
                $"GpuAddress=0x{Info.GpuAddress:X}"
            );
        }
    }
}