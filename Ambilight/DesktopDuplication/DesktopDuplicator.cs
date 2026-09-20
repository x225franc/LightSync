using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Ambilight.Extensions;

using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Rectangle = SharpDX.Mathematics.Interop.RawRectangle;
using FpsLogger = Ambilight.Util.FpsLogger;

namespace Ambilight.DesktopDuplication
{
    /// <summary>
    /// Provides access to frame-by-frame updates of a particular desktop (i.e. one monitor), with image and cursor information.
    /// </summary>
    public class DesktopDuplicator : IDisposable
    {
        private readonly Device _device;
        private OutputDescription _outputDescription;
        private readonly OutputDuplication _outputDuplication;

        private Texture2D _stagingTexture;
        private Texture2D _smallerTexture;
        private ShaderResourceView _smallerTextureView;

        /// <summary>
        /// Duplicates the output of the specified monitor on the specified graphics adapter.
        /// </summary>
        /// <param name="whichGraphicsCardAdapter">The adapter which contains the desired outputs.</param>
        /// <param name="whichOutputDevice">The output device to duplicate (i.e. monitor). Begins with zero, which seems to correspond to the primary monitor.</param>
        public DesktopDuplicator(int whichGraphicsCardAdapter, int whichOutputDevice)
        {
            Adapter1 adapter;
            try
            {
                adapter = new Factory1().GetAdapter1(whichGraphicsCardAdapter);
            }
            catch (SharpDXException ex)
            {
                throw new DesktopDuplicationException("Could not find the specified graphics card adapter.", ex);
            }
            _device = new Device(adapter);
            Output output;
            try
            {
                output = adapter.GetOutput(whichOutputDevice);
            }
            catch (SharpDXException ex)
            {
                throw new DesktopDuplicationException("Could not find the specified output device.", ex);
            }
            var output1 = output.QueryInterface<Output1>();
            _outputDescription = output.Description;

            try
            {
                _outputDuplication = output1.DuplicateOutput(_device);
            }
            catch (SharpDXException ex)
            {
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.NotCurrentlyAvailable.Result.Code)
                {
                    throw new DesktopDuplicationException(
                        "There is already the maximum number of applications using the Desktop Duplication API running, please close one of the applications and try again.");
                }

                // Any other failure (e.g. right after a resolution change, display
                // reconfiguration or waking from sleep) also means this duplicator
                // instance is unusable. Previously this was silently swallowed,
                // leaving _outputDuplication null and causing NullReferenceExceptions
                // down the line instead of a clean, catchable error.
                throw new DesktopDuplicationException("Could not duplicate the output device.", ex);
            }
        }

        private readonly FpsLogger _desktopFrameLogger = new FpsLogger("DesktopDuplication");

        /// <summary>
        /// Retrieves the latest desktop image and associated metadata.
        /// </summary>
        public Bitmap GetLatestFrame(Bitmap reusableImage)
        {
            try
            {
                // Try to get the latest frame; this may timeout
                var succeeded = RetrieveFrame();
                if (!succeeded)
                    return null;

                _desktopFrameLogger.TrackSingleFrame();

                return ProcessFrame(reusableImage);
            }
            catch (DesktopDuplicationException)
            {
                // Let this bubble up: it signals the capture context is dead
                // (DXGI access lost / device removed) and the caller needs to
                // dispose this instance and create a fresh one.
                throw;
            }
            catch (SharpDXException ex)
            {
                // Log the error but handle properly
                Debug.WriteLine($"SharpDX error in GetLatestFrame: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                // Log any other exception
                Debug.WriteLine($"Unexpected error in GetLatestFrame: {ex.Message}");
                return null;
            }
        }

        private const int mipMapLevel = 2;
        private const int scalingFactor = 1 << mipMapLevel;

        private bool RetrieveFrame()
        {
            if (_device == null || _device.IsDisposed)
            {
                Debug.WriteLine("Device is null or disposed");
                return false;
            }

            var desktopWidth = _outputDescription.DesktopBounds.GetWidth();
            var desktopHeight = _outputDescription.DesktopBounds.GetHeight();

            try
            {
                if (_stagingTexture == null || _stagingTexture.IsDisposed)
                {
                    _stagingTexture = new Texture2D(_device, new Texture2DDescription()
                    {
                        CpuAccessFlags = CpuAccessFlags.Read,
                        BindFlags = BindFlags.None,
                        Format = Format.B8G8R8A8_UNorm,
                        Width = desktopWidth / scalingFactor,
                        Height = desktopHeight / scalingFactor,
                        OptionFlags = ResourceOptionFlags.None,
                        MipLevels = 1,
                        ArraySize = 1,
                        SampleDescription = { Count = 1, Quality = 0 },
                        Usage = ResourceUsage.Staging // << can be read by CPU
                    });
                }

                SharpDX.DXGI.Resource desktopResource = null;
                try
                {
                    if (_outputDuplication == null) throw new Exception("_outputDuplication is null");
                    _outputDuplication.AcquireNextFrame(500, out var frameInformation, out desktopResource);

                    if (desktopResource == null) throw new Exception("desktopResource is null");

                    if (_smallerTexture == null || _smallerTexture.IsDisposed)
                    {
                        if (_smallerTextureView != null && !_smallerTextureView.IsDisposed)
                        {
                            _smallerTextureView.Dispose();
                            _smallerTextureView = null;
                        }

                        _smallerTexture = new Texture2D(_device, new Texture2DDescription
                        {
                            CpuAccessFlags = CpuAccessFlags.None,
                            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                            Format = Format.B8G8R8A8_UNorm,
                            Width = desktopWidth,
                            Height = desktopHeight,
                            OptionFlags = ResourceOptionFlags.GenerateMipMaps,
                            MipLevels = mipMapLevel + 1,
                            ArraySize = 1,
                            SampleDescription = { Count = 1, Quality = 0 },
                            Usage = ResourceUsage.Default
                        });
                        _smallerTextureView = new ShaderResourceView(_device, _smallerTexture);
                    }

                    using (var tempTexture = desktopResource.QueryInterface<Texture2D>())
                    {
                        if (_device == null) throw new Exception("_device is null");
                        if (_device.ImmediateContext == null) throw new Exception("_device.ImmediateContext is null");

                        _device.ImmediateContext.CopySubresourceRegion(tempTexture, 0, null, _smallerTexture, 0);
                    }

                    // Generates the mipmap of the screen
                    if (_smallerTextureView != null && !_smallerTextureView.IsDisposed &&
                        _device != null && _device.ImmediateContext != null)
                    {
                        _device.ImmediateContext.GenerateMips(_smallerTextureView);

                        // Copy the mipmap 1 of smallerTexture (size/2) to the staging texture
                        _device.ImmediateContext.CopySubresourceRegion(_smallerTexture, mipMapLevel, null, _stagingTexture, 0);
                    }

                    return true;
                }
                catch (SharpDXException ex)
                {
                    if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                    {
                        return false;
                    }

                    if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code ||
                        ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceRemoved.Result.Code ||
                        ex.ResultCode.Code == SharpDX.DXGI.ResultCode.InvalidCall.Result.Code)
                    {
                        // These specifically mean the capture device/context is no longer
                        // valid (display sleep/wake, resolution or monitor topology change).
                        // Surface a typed exception so the reader recreates the duplicator
                        // straight away instead of retrying against a dead device.
                        Debug.WriteLine($"DXGI context lost in RetrieveFrame: {ex.Message}");
                        throw new DesktopDuplicationException("DXGI capture context was lost.", ex);
                    }

                    Debug.WriteLine($"SharpDX error in RetrieveFrame: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unexpected error in RetrieveFrame: {ex.Message}");
                    return false;
                }
                finally
                {
                    // Always release frame and dispose resource
                    try
                    {
                        _outputDuplication?.ReleaseFrame();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error releasing frame: {ex.Message}");
                    }

                    desktopResource?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Critical error in RetrieveFrame: {ex.Message}");
                return false;
            }
        }

        private Bitmap ProcessFrame(Bitmap reusableImage)
        {
            if (_device == null || _device.ImmediateContext == null || _stagingTexture == null ||
                _stagingTexture.IsDisposed || _device.IsDisposed)
            {
                return null;
            }

            try
            {
                // Get the desktop capture texture
                var mapSource = _device.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, MapFlags.None);

                Bitmap image;
                var width = _outputDescription.DesktopBounds.GetWidth() / scalingFactor;
                var height = _outputDescription.DesktopBounds.GetHeight() / scalingFactor;

                if (reusableImage != null && reusableImage.Width == width && reusableImage.Height == height)
                {
                    image = reusableImage;
                }
                else
                {
                    image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                }

                var boundsRect = new System.Drawing.Rectangle(0, 0, width, height);

                try
                {
                    // Copy pixels from screen capture Texture to GDI bitmap
                    var mapDest = image.LockBits(boundsRect, ImageLockMode.WriteOnly, image.PixelFormat);
                    var sourcePtr = mapSource.DataPointer;
                    var destPtr = mapDest.Scan0;

                    try
                    {
                        if (mapSource.RowPitch == mapDest.Stride)
                        {
                            //fast copy
                            Utilities.CopyMemory(destPtr, sourcePtr, height * mapDest.Stride);
                        }
                        else
                        {
                            //safe copy
                            for (int y = 0; y < height; y++)
                            {
                                // Copy a single line 
                                Utilities.CopyMemory(destPtr, sourcePtr, width * 4);

                                // Advance pointers
                                sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                                destPtr = IntPtr.Add(destPtr, mapDest.Stride);
                            }
                        }
                    }
                    finally
                    {
                        // Always unlock bits
                        image.UnlockBits(mapDest);
                    }
                }
                finally
                {
                    // Always unmap
                    _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                }

                return image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in ProcessFrame: {ex.Message}");
                return null;
            }
        }

        public bool IsDisposed { get; private set; }

        public static int ScalingFactor => scalingFactor;

        public void Dispose()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;

                try
                {
                    _smallerTextureView?.Dispose();
                    _smallerTexture?.Dispose();
                    _stagingTexture?.Dispose();
                    _outputDuplication?.Dispose();
                    _device?.Dispose();
                    _desktopFrameLogger?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during Dispose: {ex.Message}");
                }
            }
        }
    }
}
