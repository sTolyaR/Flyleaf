﻿using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;

namespace DoubleVideoViewOverlay
{
    /// <summary>
    /// Testing VideoView within another VideoView
    /// 
    /// This sample demonstrates a second videoview which follow the first videoview's input and preview's the seeking position before the actual seeking
    /// </summary>
    public partial class MainWindow : Window
    {
        public Player Player        { get; set; }
        public Player PlayerSeek    { get; set; }
        public bool   IsSeeking     { get; set; }

        public string SampleVideo   { get; set; } = Utils.FindFileBelow("Sample.mp4");

        Binding sliderBinding;

        public MainWindow()
        {
            // Initializes Engine (Specifies FFmpeg libraries path which is required)
            Engine.Start(new EngineConfig()
            {
                #if DEBUG
                LogOutput       = ":debug",
                LogLevel        = LogLevel.Debug,
                FFmpegLogLevel  = FFmpegLogLevel.Warning,
                #endif
                
                PluginsPath     = ":Plugins",
                FFmpegPath      = ":FFmpeg",

                // Use UIRefresh to update Stats/BufferDuration (and CurTime more frequently than a second)
                UIRefresh       = true,
                UIRefreshInterval= 100,
                UICurTimePerSecond = false // If set to true it updates when the actual timestamps second change rather than a fixed interval
            });

            InitializeComponent();

            Player = new Player();
            PlayerSeek = new Player();

            PlayerSeek.Config.Player.KeyBindings.Enabled = false;
            PlayerSeek.Config.Player.MouseBindings.Enabled = false;
            PlayerSeek.Config.Audio.Enabled = false;
            PlayerSeek.Config.Player.AutoPlay = false;
            //PlayerSeek.Config.Video.AspectRatio = AspectRatio.Fill;

            DataContext = this;

            Player.OpenCompleted += Player_OpenCompleted;

            sliderBinding = new Binding("Player.CurTime");
            sliderBinding.Mode = BindingMode.OneWay;
        }

        private void Player_OpenCompleted(object sender, OpenCompletedArgs e)
        {
            if (!e.Success)
                return;

            PlayerSeek.Open(Player.decoder.UserInputUrl);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // TBR: Binding should work directly
            SeekView.Player = PlayerSeek;
        }

        private void Slider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed && !IsSeeking)
            {
                BindingOperations.ClearBinding(SliderSeek, Slider.ValueProperty);
                SeekView.Visibility = Visibility.Visible;
                IsSeeking = true;
            }
        }

        private void Slider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (IsSeeking)
            {
                SeekView.Visibility = Visibility.Collapsed;
                Player.SeekAccurate((int) (PlayerSeek.CurTime / 10000));
                BindingOperations.SetBinding(SliderSeek, Slider.ValueProperty, sliderBinding);
            }

            IsSeeking = false;
        }
        
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsSeeking)
                return;

            PlayerSeek.SeekAccurate((int) (e.NewValue / 10000));
        }
    }
}