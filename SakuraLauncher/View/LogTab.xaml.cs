﻿using SakuraLauncher.Model;
using SakuraLibrary.Proto;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SakuraLauncher.View
{
    /// <summary>
    /// LogTab.xaml 的交互逻辑
    /// </summary>
    public partial class LogTab : UserControl
    {
        private readonly LauncherViewModel Model;

        private bool AutoScroll = true;

        public LogTab(LauncherViewModel main)
        {
            InitializeComponent();
            DataContext = Model = main;
        }

        private void ButtonClear_Click(object sender, RoutedEventArgs e)
        {
            Model.ClearLog();
            Model.Pipe.Request(MessageID.LogClear);
        }

        private void ButtonSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog()
                {
                    FileName = "SakuraLauncher_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    DefaultExt = ".log",
                    Filter = "日志文件|*.log"
                };
                if (dlg.ShowDialog() != true)
                {
                    return;
                }
                File.WriteAllText(dlg.FileName, string.Join("\n", Model.Logs.Select(v => v.ToString())), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Model.SimpleFailureHandler(false, ex.ToString());
            }
        }

        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.Source is ScrollViewer sv)
            {
                if (e.ExtentHeightChange == 0)
                {
                    AutoScroll = sv.VerticalOffset == sv.ScrollableHeight;
                }
                else if (AutoScroll)
                {
                    sv.ScrollToVerticalOffset(sv.ExtentHeight);
                }
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Model.LogsViewSource.Refresh();
    }
}
