﻿using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;
using static FFmpeg.AutoGen.ffmpegEx;

using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.MediaFramework.MediaStream;

public unsafe class VideoStream : StreamBase
{
    public AspectRatio                  AspectRatio         { get; set; }
    public ColorRange                   ColorRange          { get; set; }
    public ColorSpace                   ColorSpace          { get; set; }
    public AVColorTransferCharacteristic
                                        ColorTransfer       { get; set; }
    public double                       Rotation            { get; set; }
    public double                       FPS                 { get; set; }
    public long                         FrameDuration       { get ;set; }
    public int                          Height              { get; set; }
    public bool                         IsRGB               { get; set; }
    public AVComponentDescriptor[]      PixelComps          { get; set; }
    public int                          PixelComp0Depth     { get; set; }
    public AVPixelFormat                PixelFormat         { get; set; }
    public AVPixFmtDescriptor*          PixelFormatDesc     { get; set; }
    public string                       PixelFormatStr      { get; set; }
    public int                          PixelPlanes         { get; set; }
    public bool                         PixelSameDepth      { get; set; }
    public bool                         PixelInterleaved    { get; set; }
    public int                          TotalFrames         { get; set; }
    public int                          Width               { get; set; }
    public bool                         FixTimestamps       { get; set; } // TBR: For formats such as h264/hevc that have no or invalid pts values

    public override string GetDump()
        => $"[{Type} #{StreamIndex}] {Codec} {PixelFormatStr} {Width}x{Height} @ {FPS:#.###} | [Color: {ColorSpace}] [BR: {BitRate}] | {Utils.TicksToTime((long)(AVStream->start_time * Timebase))}/{Utils.TicksToTime((long)(AVStream->duration * Timebase))} | {Utils.TicksToTime(StartTime)}/{Utils.TicksToTime(Duration)}";

    public VideoStream() { }
    public VideoStream(Demuxer demuxer, AVStream* st) : base(demuxer, st)
    {
        Demuxer = demuxer;
        AVStream = st;
        Refresh();
    }

    public void Refresh(AVPixelFormat format = AVPixelFormat.AV_PIX_FMT_NONE)
    {
        base.Refresh();

        PixelFormat     = format == AVPixelFormat.AV_PIX_FMT_NONE ? (AVPixelFormat)AVStream->codecpar->format : format;
        PixelFormatStr  = PixelFormat.ToString().Replace("AV_PIX_FMT_","").ToLower();
        Width           = AVStream->codecpar->width;
        Height          = AVStream->codecpar->height;
        FPS             = av_q2d(AVStream->avg_frame_rate) > 0 ? av_q2d(AVStream->avg_frame_rate) : av_q2d(AVStream->r_frame_rate);
        FrameDuration   = FPS > 0 ? (long) (10000000 / FPS) : 0;
        TotalFrames     = AVStream->duration > 0 && FrameDuration > 0 ? (int) (AVStream->duration * Timebase / FrameDuration) : (FrameDuration > 0 ? (int) (Demuxer.Duration / FrameDuration) : 0);

        // TBR: Maybe required also for input formats with AVFMT_NOTIMESTAMPS (and audio/subs) 
        // Possible FFmpeg.Autogen bug with Demuxer.FormatContext->iformat->flags (should be uint?) does not contain AVFMT_NOTIMESTAMPS (256 instead of 384)
        if (Demuxer.Name == "h264" || Demuxer.Name == "hevc")
        {
            FixTimestamps = true;

            if (FPS == 0)
            {
                FPS = 25;
                FrameDuration = (long) (10000000 / FPS);
            }
        }
        else
            FixTimestamps = false;

        int x, y;
        AVRational sar = av_guess_sample_aspect_ratio(null, AVStream, null);
        if (av_cmp_q(sar, av_make_q(0, 1)) <= 0)
            sar = av_make_q(1, 1);

        av_reduce(&x, &y, Width  * sar.num, Height * sar.den, 1024 * 1024);
        AspectRatio = new AspectRatio(x, y);

        if (PixelFormat != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            ColorRange = AVStream->codecpar->color_range == AVColorRange.AVCOL_RANGE_JPEG ? ColorRange.Full : ColorRange.Limited;

            if (AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT470BG)
                ColorSpace = ColorSpace.BT601;
            else if (AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT709)
                ColorSpace = ColorSpace.BT709;
            else ColorSpace = AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT2020_CL || AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT2020_NCL
                ? ColorSpace.BT2020
                : Height > 576 ? ColorSpace.BT709 : ColorSpace.BT601;

            // This causes issues
            //if (AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_UNSPECIFIED && AVStream->codecpar->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED && Height > 1080)
            //{   // TBR: Handle Dolphy Vision?
            //    ColorSpace = ColorSpace.BT2020;
            //    ColorTransfer = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
            //}
            //else
            ColorTransfer = AVStream->codecpar->color_trc;

            Rotation = av_display_rotation_get(av_stream_get_side_data(AVStream, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX, null));

            PixelFormatDesc = av_pix_fmt_desc_get(PixelFormat);
            var comps       = PixelFormatDesc->comp.ToArray();
            PixelComps      = new AVComponentDescriptor[PixelFormatDesc->nb_components];
            for (int i=0; i<PixelComps.Length; i++)
                PixelComps[i] = comps[i];

            PixelInterleaved= PixelFormatDesc->log2_chroma_w != PixelFormatDesc->log2_chroma_h;
            IsRGB           = (PixelFormatDesc->flags & AV_PIX_FMT_FLAG_RGB) != 0;

            PixelSameDepth  = true;
            PixelPlanes     = 0;
            if (PixelComps.Length > 0)
            {
                PixelComp0Depth = PixelComps[0].depth;
                int prevBit     = PixelComp0Depth;
                for (int i=0; i<PixelComps.Length; i++)
                {
                    if (PixelComps[i].plane > PixelPlanes)
                        PixelPlanes = PixelComps[i].plane;

                    if (prevBit != PixelComps[i].depth)
                        PixelSameDepth = false;
                }

                PixelPlanes++;
            }
        }
    }
}