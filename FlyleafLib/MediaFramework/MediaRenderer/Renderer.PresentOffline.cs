﻿using System;
using System.Drawing.Imaging;
using System.Drawing;
using System.Threading;

using SharpGen.Runtime;

using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public partial class Renderer
{
    // Used for off screen rendering
    Texture2DDescription                    singleStageDesc, singleGpuDesc;
    ID3D11Texture2D                         singleStage;
    ID3D11Texture2D                         singleGpu;
    ID3D11RenderTargetView                  singleGpuRtv;
    Viewport                                singleViewport;

    // Used for parallel off screen rendering
    ID3D11RenderTargetView[]                rtv2;
    ID3D11Texture2D[]                       backBuffer2;
    bool[]                                  backBuffer2busy;

    unsafe internal void PresentOffline(VideoFrame frame, ID3D11RenderTargetView rtv, Viewport viewport)
    {
        if (videoProcessor == VideoProcessors.D3D11)
        {
            vd1.CreateVideoProcessorOutputView(rtv.Resource, vpe, vpovd, out var vpov);

            RawRect rect = new((int)viewport.X, (int)viewport.Y, (int)(viewport.Width + viewport.X), (int)(viewport.Height + viewport.Y));
            vc.VideoProcessorSetStreamSourceRect(vp, 0, true, VideoRect);
            vc.VideoProcessorSetStreamDestRect(vp, 0, true, rect);
            vc.VideoProcessorSetOutputTargetRect(vp, true, rect);

            if (frame.bufRef != null)
            {
                vpivd.Texture2D.ArraySlice = frame.subresource;
                vd1.CreateVideoProcessorInputView(VideoDecoder.textureFFmpeg, vpe, vpivd, out vpiv);
            }
            else
            {
                vpivd.Texture2D.ArraySlice = 0;
                vd1.CreateVideoProcessorInputView(frame.textures[0], vpe, vpivd, out vpiv);
            }

            vpsa[0].InputSurface = vpiv;
            vc.VideoProcessorBlt(vp, vpov, 0, 1, vpsa);
            vpiv.Dispose();
            vpov.Dispose();
        }
        else
        {
            context.OMSetRenderTargets(rtv);
            context.ClearRenderTargetView(rtv, Config.Video._BackgroundColor);
            context.RSSetViewport(viewport);
            context.PSSetShaderResources(0, frame.srvs);
            context.Draw(6, 0);
        }
    }

    /// <summary>
    /// Gets bitmap from a video frame
    /// </summary>
    /// <param name="width">Specify the width (-1: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (-1: will keep the ratio based on width)</param>
    /// <param name="frame">Video frame to process (null: will use the current/last frame)</param>
    /// <returns></returns>
    unsafe public Bitmap GetBitmap(int width = -1, int height = -1, VideoFrame frame = null)
    {
        try
        {
            lock (lockDevice)
            {
                frame ??= LastFrame;

                if (Disposed || frame == null || (frame.textures == null && frame.bufRef == null))
                    return null;

                if (width == -1 && height == -1)
                {
                    width  = VideoRect.Right;
                    height = VideoRect.Bottom;
                }
                else if (width != -1 && height == -1)
                    height = (int)(width / curRatio);
                else if (height != -1 && width == -1)
                    width  = (int)(height * curRatio);

                if (singleStageDesc.Width != width || singleStageDesc.Height != height)
                {
                    singleGpu?.Dispose();
                    singleStage?.Dispose();
                    singleGpuRtv?.Dispose();

                    singleStageDesc.Width   = width;
                    singleStageDesc.Height  = height;
                    singleGpuDesc.Width     = width;
                    singleGpuDesc.Height    = height;

                    singleStage = Device.CreateTexture2D(singleStageDesc);
                    singleGpu   = Device.CreateTexture2D(singleGpuDesc);
                    singleGpuRtv= Device.CreateRenderTargetView(singleGpu);

                    singleViewport = new Viewport(width, height);
                }

                PresentOffline(frame, singleGpuRtv, singleViewport);

                if (videoProcessor == VideoProcessors.D3D11)
                    SetViewport();
            }

            context.CopyResource(singleStage, singleGpu);
            return GetBitmap(singleStage);

        } catch (Exception e)
        {
            Log.Warn($"GetBitmap failed with: {e.Message}");
            return null;
        }
    }
    public Bitmap GetBitmap(ID3D11Texture2D stageTexture)
    {
        Bitmap bitmap   = new(stageTexture.Description.Width, stageTexture.Description.Height);
        var db          = context.Map(stageTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var bitmapData  = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        if (db.RowPitch == bitmapData.Stride)
            MemoryHelpers.CopyMemory(bitmapData.Scan0, db.DataPointer, bitmap.Width * bitmap.Height * 4);
        else
        {
            var sourcePtr   = db.DataPointer;
            var destPtr     = bitmapData.Scan0;

            for (int y = 0; y < bitmap.Height; y++)
            {
                MemoryHelpers.CopyMemory(destPtr, sourcePtr, bitmap.Width * 4);

                sourcePtr   = IntPtr.Add(sourcePtr, db.RowPitch);
                destPtr     = IntPtr.Add(destPtr, bitmapData.Stride);
            }
        }

        bitmap.UnlockBits(bitmapData);
        context.Unmap(stageTexture, 0);

        return bitmap;
    }

    /// <summary>
    /// Extracts a bitmap from a video frame
    /// (Currently cannot be used in parallel with the rendering)
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public Bitmap ExtractFrame(VideoFrame frame)
    {
        if (Device == null || frame == null) return null;

        int subresource = -1;

        Texture2DDescription stageDesc = new()
        {
            Usage       = ResourceUsage.Staging,
            Width       = VideoDecoder.VideoStream.Width,
            Height      = VideoDecoder.VideoStream.Height,
            Format      = Format.B8G8R8A8_UNorm,
            ArraySize   = 1,
            MipLevels   = 1,
            BindFlags   = BindFlags.None,
            CPUAccessFlags      = CpuAccessFlags.Read,
            SampleDescription   = new SampleDescription(1, 0)
        };
        var stage = Device.CreateTexture2D(stageDesc);

        lock (lockDevice)
        {
            while (true)
            {
                for (int i=0; i<MaxOffScreenTextures; i++)
                    if (!backBuffer2busy[i]) { subresource = i; break;}

                if (subresource != -1)
                    break;
                else
                    Thread.Sleep(5);
            }

            backBuffer2busy[subresource] = true;
            PresentOffline(frame, rtv2[subresource], new Viewport(backBuffer2[subresource].Description.Width, backBuffer2[subresource].Description.Height));
            VideoDecoder.DisposeFrame(frame);

            context.CopyResource(stage, backBuffer2[subresource]);
            backBuffer2busy[subresource] = false;
        }

        var bitmap = GetBitmap(stage);
        stage.Dispose(); // TODO use array stage
        return bitmap;
    }

    private void PrepareForExtract()
    {
        if (rtv2 != null)
            for (int i = 0; i < rtv2.Length - 1; i++)
                rtv2[i].Dispose();

        if (backBuffer2 != null)
            for (int i = 0; i < backBuffer2.Length - 1; i++)
                backBuffer2[i].Dispose();

        backBuffer2busy = new bool[MaxOffScreenTextures];
        rtv2 = new ID3D11RenderTargetView[MaxOffScreenTextures];
        backBuffer2 = new ID3D11Texture2D[MaxOffScreenTextures];

        for (int i = 0; i < MaxOffScreenTextures; i++)
        {
            backBuffer2[i] = Device.CreateTexture2D(new Texture2DDescription()
            {
                Usage       = ResourceUsage.Default,
                BindFlags   = BindFlags.RenderTarget,
                Format      = Format.B8G8R8A8_UNorm,
                Width       = VideoStream.Width,
                Height      = VideoStream.Height,

                ArraySize   = 1,
                MipLevels   = 1,
                SampleDescription = new SampleDescription(1, 0)
            });

            rtv2[i] = Device.CreateRenderTargetView(backBuffer2[i]);
        }

        context.RSSetViewport(0, 0, VideoStream.Width, VideoStream.Height);
    }
}
