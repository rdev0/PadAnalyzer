﻿using System;
using System.Threading;
using System.Windows;

namespace CsScriptManaged.UI
{
    internal class InteractiveWindow : Window
    {
        public InteractiveWindow()
        {
            // Set window look
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = false;
            Title = "C# Interactive Window";

            // Add text editor
            InteractiveCodeEditor textEditor = new InteractiveCodeEditor();

            textEditor.CommandExecuted += (text, result) =>
            {
                // TODO:
                if (!string.IsNullOrEmpty(text))
                    MessageBox.Show(text);
            };
            textEditor.CommandFailed += (text, error) =>
            {
                // TODO:
                MessageBox.Show(text + error);
            };
            textEditor.Executing += (executing) =>
            {
                if (!executing)
                {
                    textEditor.TextArea.Focus();
                }
            };
            textEditor.CloseRequested += () => Close();

            Content = textEditor;
        }

        /// <summary>
        /// Shows the window as modal dialog.
        /// </summary>
        public static void ShowModalWindow()
        {
            ExecuteInSTA(() =>
            {
                Window window = null;

                try
                {
                    window = CreateWindow();
                    window.ShowDialog();
                }
                catch (ExitRequestedException)
                {
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }

                window.Close();
            });
        }

        /// <summary>
        /// Shows the window.
        /// </summary>
        public static void ShowWindow()
        {
            ExecuteInSTA(() =>
            {
                Window window = null;

                try
                {
                    window = CreateWindow();
                    window.Show();

                    var _dispatcherFrame = new System.Windows.Threading.DispatcherFrame();
                    window.Closed += (obj, e) => { _dispatcherFrame.Continue = false; };
                    System.Windows.Threading.Dispatcher.PushFrame(_dispatcherFrame);
                }
                catch (ExitRequestedException)
                {
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }

                window.Close();
            }, waitForExecution: false);
        }

        private static void ExecuteInSTA(Action action, bool waitForExecution = true)
        {
            Thread thread = new Thread(() => { action(); });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (waitForExecution)
            {
                thread.Join();
            }
        }

        private static Window CreateWindow()
        {
            return new InteractiveWindow();
        }
    }
}
