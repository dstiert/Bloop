﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Animation;
using NHotkey;
using NHotkey.Wpf;
using Bloop.Core.i18n;
using Bloop.Core.Plugin;
using Bloop.Core.Theme;
using Bloop.Core.UserSettings;
using Bloop.Helper;
using Bloop.Infrastructure;
using Bloop.Infrastructure.Hotkey;
using Bloop.Plugin;
using Bloop.Storage;
using ContextMenu = System.Windows.Forms.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Forms.MenuItem;
using MessageBox = System.Windows.MessageBox;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Bloop
{
    public partial class MainWindow : IPublicAPI
    {

        #region Properties

        private readonly Storyboard progressBarStoryboard = new Storyboard();
        private NotifyIcon notifyIcon;
        private bool queryHasReturn;
        private string lastQuery;
        private ToolTip toolTip = new ToolTip();

        private bool ignoreTextChange = false;
        private List<Result> CurrentContextMenus = new List<Result>();
        private string textBeforeEnterContextMenuMode;

        #endregion

        #region Public API

        public void ChangeQuery(string query, bool requery = false)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                tbQuery.Text = query;
                tbQuery.CaretIndex = tbQuery.Text.Length;
                if (requery)
                {
                    TextBoxBase_OnTextChanged(null, null);
                }
            }));
        }

        public void ChangeQueryText(string query, bool selectAll = false)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                ignoreTextChange = true;
                tbQuery.Text = query;
                tbQuery.CaretIndex = tbQuery.Text.Length;
                if (selectAll)
                {
                    tbQuery.SelectAll();
                }
            }));
        }

        public void CloseApp()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                notifyIcon.Visible = false;
                Close();
                Environment.Exit(0);
            }));
        }

        public void HideApp()
        {
            Dispatcher.Invoke(new Action(HideBloop));
        }

        public void ShowApp()
        {
            Dispatcher.Invoke(new Action(() => ShowBloop()));
        }

        public void ShowMsg(string title, string subTitle, string iconPath)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                var m = new Msg { Owner = GetWindow(this) };
                m.Show(title, subTitle, iconPath);
            }));
        }

        public void OpenSettingDialog(string tabName = "general")
        {
            Dispatcher.Invoke(new Action(() =>
            {
                SettingWindow sw = SingletonWindowOpener.Open<SettingWindow>(this);
                sw.SwitchTo(tabName);
            }));
        }

        public void StartLoadingBar()
        {
            Dispatcher.Invoke(new Action(StartProgress));
        }

        public void StopLoadingBar()
        {
            Dispatcher.Invoke(new Action(StopProgress));
        }

        public void InstallPlugin(string path)
        {
            Dispatcher.Invoke(new Action(() => PluginManager.InstallPlugin(path)));
        }

        public void ReloadPlugins()
        {
            Dispatcher.Invoke(new Action(() => PluginManager.Init(this)));
        }

        public string GetTranslation(string key)
        {
            return InternationalizationManager.Instance.GetTranslation(key);
        }

        public List<PluginPair> GetAllPlugins()
        {
            return PluginManager.AllPlugins;
        }

        public event BloopKeyDownEventHandler BackKeyDownEvent;
        public event BloopGlobalKeyboardEventHandler GlobalKeyboardEvent;
        public event ResultItemDropEventHandler ResultItemDropEvent;

        public void PushResults(Query query, PluginMetadata plugin, List<Result> results)
        {
            results.ForEach(o =>
            {
                o.PluginDirectory = plugin.PluginDirectory;
                o.PluginID = plugin.ID;
                o.OriginQuery = query;
            });
            UpdateResultView(results);
        }

        #endregion

        public MainWindow()
        {
            InitializeComponent();
            ThreadPool.SetMaxThreads(30, 10);
            ThreadPool.SetMinThreads(10, 5);

            WebRequest.RegisterPrefix("data", new DataWebRequestFactory());
            GlobalHotkey.Instance.hookedKeyboardCallback += KListener_hookedKeyboardCallback;
            progressBar.ToolTip = toolTip;
            pnlResult.LeftMouseClickEvent += SelectResult;
            pnlResult.ItemDropEvent += pnlResult_ItemDropEvent;
            pnlContextMenu.LeftMouseClickEvent += SelectResult;
            pnlResult.RightMouseClickEvent += pnlResult_RightMouseClickEvent;

            ThemeManager.Theme.ChangeTheme(UserSettingStorage.Instance.Theme);
            InternationalizationManager.Instance.ChangeLanguage(UserSettingStorage.Instance.Language);

            SetHotkey(UserSettingStorage.Instance.Hotkey, OnHotkey);
            SetCustomPluginHotkey();
            InitialTray();

            Closing += MainWindow_Closing;
            //since MainWIndow implement IPublicAPI, so we need to finish ctor MainWindow object before
            //PublicAPI invoke in plugin init methods. E.g FolderPlugin
            ThreadPool.QueueUserWorkItem(o =>
            {
                Thread.Sleep(50);
                PluginManager.Init(this);
            });
            ThreadPool.QueueUserWorkItem(o =>
            {
                Thread.Sleep(50);
                PreLoadImages();
            });
        }

        void pnlResult_ItemDropEvent(Result result, IDataObject dropDataObject, DragEventArgs args)
        {
            PluginPair pluginPair = PluginManager.AllPlugins.FirstOrDefault(o => o.Metadata.ID == result.PluginID);
            if (ResultItemDropEvent != null && pluginPair != null)
            {
                foreach (var delegateHandler in ResultItemDropEvent.GetInvocationList())
                {
                    if (delegateHandler.Target == pluginPair.Plugin)
                    {
                        delegateHandler.DynamicInvoke(result, dropDataObject, args);
                    }
                }
            }
        }

        private bool KListener_hookedKeyboardCallback(KeyEvent keyevent, int vkcode, SpecialKeyState state)
        {
            if (GlobalKeyboardEvent != null)
            {
                return GlobalKeyboardEvent((int)keyevent, vkcode, state);
            }
            return true;
        }

        private void PreLoadImages()
        {
            ImageLoader.ImageLoader.PreloadImages();
        }

        void pnlResult_RightMouseClickEvent(Result result)
        {
            ShowContextMenu(result);
        }

        void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            UserSettingStorage.Instance.WindowLeft = Left;
            UserSettingStorage.Instance.WindowTop = Top;
            UserSettingStorage.Instance.Save();
            this.HideBloop();
            e.Cancel = true;
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            Left = GetWindowsLeft();
            Top = GetWindowsTop();

            InitProgressbarAnimation();
            WindowIntelopHelper.DisableControlBox(this);
        }

        private double GetWindowsLeft()
        {
            var source = PresentationSource.FromVisual(this);
            var screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var dpi = (source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.M11 : 1);
            var workingWidth = (int)Math.Floor(screen.WorkingArea.Width * dpi);
            if (UserSettingStorage.Instance.RememberLastLaunchLocation)
            {
                var origScreen = Screen.FromRectangle(new Rectangle((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight));
                var coordX = (Left - origScreen.WorkingArea.Left) / (origScreen.WorkingArea.Width - ActualWidth);
                UserSettingStorage.Instance.WindowLeft = (workingWidth - ActualWidth) * coordX + screen.WorkingArea.Left;
            }
            else
            {
                UserSettingStorage.Instance.WindowLeft = (workingWidth - ActualWidth) / 2 + screen.WorkingArea.Left;
            }

            return UserSettingStorage.Instance.WindowLeft;
        }

        private double GetWindowsTop()
        {
            var source = PresentationSource.FromVisual(this);
            var screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var dpi = (source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.M11 : 1);
            var workingHeight = (int)Math.Floor(screen.WorkingArea.Height * dpi);
            if (UserSettingStorage.Instance.RememberLastLaunchLocation)
            {
                var origScreen = Screen.FromRectangle(new Rectangle((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight));
                var coordY = (Top - origScreen.WorkingArea.Top) / (origScreen.WorkingArea.Height - ActualHeight);
                UserSettingStorage.Instance.WindowTop = (workingHeight - ActualHeight) * coordY + screen.WorkingArea.Top;
            }
            else
            {
                UserSettingStorage.Instance.WindowTop = (workingHeight - tbQuery.ActualHeight) / 4 + screen.WorkingArea.Top;
            }
            return UserSettingStorage.Instance.WindowTop;
        }

        void OnUpdateError(object sender, EventArgs e)
        {
            string updateError = InternationalizationManager.Instance.GetTranslation("update_Bloop_update_error");
            MessageBox.Show(updateError);
        }

        public void SetHotkey(string hotkeyStr, EventHandler<HotkeyEventArgs> action)
        {
            var hotkey = new HotkeyModel(hotkeyStr);
            SetHotkey(hotkey, action);
        }

        public void SetHotkey(HotkeyModel hotkey, EventHandler<HotkeyEventArgs> action)
        {
            string hotkeyStr = hotkey.ToString();
            try
            {
                HotkeyManager.Current.AddOrReplace(hotkeyStr, hotkey.CharKey, hotkey.ModifierKeys, action);
            }
            catch (Exception)
            {
                string errorMsg = string.Format(InternationalizationManager.Instance.GetTranslation("registerHotkeyFailed"), hotkeyStr);
                MessageBox.Show(errorMsg);
            }
        }

        public void RemoveHotkey(string hotkeyStr)
        {
            if (!string.IsNullOrEmpty(hotkeyStr))
            {
                HotkeyManager.Current.Remove(hotkeyStr);
            }
        }

        private void SetCustomPluginHotkey()
        {
            if (UserSettingStorage.Instance.CustomPluginHotkeys == null) return;
            foreach (CustomPluginHotkey hotkey in UserSettingStorage.Instance.CustomPluginHotkeys)
            {
                CustomPluginHotkey hotkey1 = hotkey;
                SetHotkey(hotkey.Hotkey, delegate
                {
                    ShowApp();
                    ChangeQuery(hotkey1.ActionKeyword, true);
                });
            }
        }

        private void OnHotkey(object sender, HotkeyEventArgs e)
        {
            ToggleBloop();
            e.Handled = true;
        }

        public void ToggleBloop()
        {
            if (!IsVisible)
            {
                ShowBloop();
            }
            else
            {
                HideBloop();
            }
        }

        private void InitProgressbarAnimation()
        {
            var da = new DoubleAnimation(progressBar.X2, ActualWidth + 100, new Duration(new TimeSpan(0, 0, 0, 0, 1600)));
            var da1 = new DoubleAnimation(progressBar.X1, ActualWidth, new Duration(new TimeSpan(0, 0, 0, 0, 1600)));
            Storyboard.SetTargetProperty(da, new PropertyPath("(Line.X2)"));
            Storyboard.SetTargetProperty(da1, new PropertyPath("(Line.X1)"));
            progressBarStoryboard.Children.Add(da);
            progressBarStoryboard.Children.Add(da1);
            progressBarStoryboard.RepeatBehavior = RepeatBehavior.Forever;
            progressBar.Visibility = Visibility.Hidden;
            progressBar.BeginStoryboard(progressBarStoryboard);
        }

        private void InitialTray()
        {
            notifyIcon = new NotifyIcon { Text = "Bloop", Icon = Properties.Resources.app, Visible = true };
            notifyIcon.Click += (o, e) => ShowBloop();
            var open = new MenuItem(GetTranslation("iconTrayOpen"));
            open.Click += (o, e) => ShowBloop();
            var setting = new MenuItem(GetTranslation("iconTraySettings"));
            setting.Click += (o, e) => OpenSettingDialog();
            var about = new MenuItem(GetTranslation("iconTrayAbout"));
            about.Click += (o, e) => OpenSettingDialog("about");
            var exit = new MenuItem(GetTranslation("iconTrayExit"));
            exit.Click += (o, e) => CloseApp();
            MenuItem[] childen = { open, setting, about, exit };
            notifyIcon.ContextMenu = new ContextMenu(childen);
        }

        private void QueryContextMenu()
        {
            pnlContextMenu.Clear();
            var query = tbQuery.Text.ToLower();
            if (string.IsNullOrEmpty(query))
            {
                pnlContextMenu.AddResults(CurrentContextMenus);
            }
            else
            {
                List<Result> filterResults = new List<Result>();
                foreach (Result contextMenu in CurrentContextMenus)
                {
                    if (StringMatcher.IsMatch(contextMenu.Title, query)
                        || StringMatcher.IsMatch(contextMenu.SubTitle, query))
                    {
                        filterResults.Add(contextMenu);
                    }
                }
                pnlContextMenu.AddResults(filterResults);
            }
        }

        private void TextBoxBase_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (ignoreTextChange) { ignoreTextChange = false; return; }

            toolTip.IsOpen = false;
            pnlResult.Dirty = true;

            if (IsInContextMenuMode)
            {
                QueryContextMenu();
                return;
            }

            lastQuery = tbQuery.Text;
            int searchDelay = GetSearchDelay(lastQuery);

            Dispatcher.DelayInvoke("UpdateSearch",
                o =>
                {
                    Dispatcher.DelayInvoke("ClearResults", i =>
                    {
                        // first try to use clear method inside pnlResult, which is more closer to the add new results
                        // and this will not bring splash issues.After waiting 100ms, if there still no results added, we
                        // must clear the result. otherwise, it will be confused why the query changed, but the results
                        // didn't.
                        if (pnlResult.Dirty) pnlResult.Clear();
                    }, TimeSpan.FromMilliseconds(100), null);
                    queryHasReturn = false;
                    Query query = new Query(lastQuery);
                    query.IsIntantQuery = searchDelay == 0;
                    Query(query);
                    Dispatcher.DelayInvoke("ShowProgressbar", originQuery =>
                    {
                        if (!queryHasReturn && originQuery == tbQuery.Text && !string.IsNullOrEmpty(lastQuery))
                        {
                            StartProgress();
                        }
                    }, TimeSpan.FromMilliseconds(150), tbQuery.Text);
                    //reset query history index after user start new query
                    ResetQueryHistoryIndex();
                }, TimeSpan.FromMilliseconds(searchDelay));
        }

        private void ResetQueryHistoryIndex()
        {
            QueryHistoryStorage.Instance.Reset();
        }

        private int GetSearchDelay(string query)
        {
            if (!string.IsNullOrEmpty(query) && PluginManager.IsInstantQuery(query))
            {
                DebugHelper.WriteLine("execute query without delay");
                return 0;
            }

            DebugHelper.WriteLine("execute query with 200ms delay");
            return 200;
        }

        private void Query(Query q)
        {
            PluginManager.Query(q);
            StopProgress();
        }

        private void BackToResultMode()
        {
            ChangeQueryText(textBeforeEnterContextMenuMode);
            pnlResult.Visibility = Visibility.Visible;
            pnlContextMenu.Visibility = Visibility.Collapsed;
        }

        private void Border_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void StartProgress()
        {
            progressBar.Visibility = Visibility.Visible;
        }

        private void StopProgress()
        {
            progressBar.Visibility = Visibility.Hidden;
        }

        private void HideBloop()
        {
            if (IsInContextMenuMode)
            {
                BackToResultMode();
            }
            Hide();
        }

        private void ShowBloop(bool selectAll = true)
        {
            UserSettingStorage.Instance.IncreaseActivateTimes();
            Left = GetWindowsLeft();
            Top = GetWindowsTop();

            Show();
            Activate();
            Focus();
            tbQuery.Focus();
            ResetQueryHistoryIndex();
            if (selectAll) tbQuery.SelectAll();
        }

        private void MainWindow_OnDeactivated(object sender, EventArgs e)
        {
            if (UserSettingStorage.Instance.HideWhenDeactive)
            {
                HideBloop();
            }
        }

        private void TbQuery_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            //when alt is pressed, the real key should be e.SystemKey
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);
            switch (key)
            {
                case Key.Escape:
                    if (IsInContextMenuMode)
                    {
                        BackToResultMode();
                    }
                    else
                    {
                        HideBloop();
                    }
                    e.Handled = true;
                    break;

                case Key.Tab:
                    if (GlobalHotkey.Instance.CheckModifiers().ShiftPressed)
                    {
                        SelectPrevItem();
                    }
                    else
                    {
                        SelectNextItem();
                    }
                    e.Handled = true;
                    break;

                case Key.N:
                case Key.J:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        SelectNextItem();
                    }
                    break;

                case Key.P:
                case Key.K:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        SelectPrevItem();
                    }
                    break;

                case Key.O:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        if (IsInContextMenuMode)
                        {
                            BackToResultMode();
                        }
                        else
                        {
                            ShowContextMenu(GetActiveResult());
                        }
                    }
                    break;

                case Key.Down:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        DisplayNextQuery();
                    }
                    else
                    {
                        SelectNextItem();
                    }
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        DisplayPrevQuery();
                    }
                    else
                    {
                        SelectPrevItem();
                    }
                    e.Handled = true;
                    break;

                case Key.D:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        pnlResult.SelectNextPage();
                    }
                    break;

                case Key.PageDown:
                    pnlResult.SelectNextPage();
                    e.Handled = true;
                    break;

                case Key.U:
                    if (GlobalHotkey.Instance.CheckModifiers().CtrlPressed)
                    {
                        pnlResult.SelectPrevPage();
                    }
                    break;

                case Key.PageUp:
                    pnlResult.SelectPrevPage();
                    e.Handled = true;
                    break;

                case Key.Back:
                    if (BackKeyDownEvent != null)
                    {
                        BackKeyDownEvent(new BloopKeyDownEventArgs()
                        {
                            Query = tbQuery.Text,
                            keyEventArgs = e
                        });
                    }
                    break;

                case Key.Enter:
                    Result activeResult = GetActiveResult();
                    if (GlobalHotkey.Instance.CheckModifiers().ShiftPressed)
                    {
                        ShowContextMenu(activeResult);
                    }
                    else
                    {
                        SelectResult(activeResult);
                    }
                    e.Handled = true;
                    break;

                case Key.D1:
                    SelectItem(1);
                    break;

                case Key.D2:
                    SelectItem(2);
                    break;

                case Key.D3:
                    SelectItem(3);
                    break;

                case Key.D4:
                    SelectItem(4);
                    break;

                case Key.D5:
                    SelectItem(5);
                    break;
                case Key.D6:
                    SelectItem(6);
                    break;

            }
        }

        private void DisplayPrevQuery()
        {
            var prev = QueryHistoryStorage.Instance.Previous();
            DisplayQueryHistory(prev);
        }

        private void DisplayNextQuery()
        {
            var nextQuery = QueryHistoryStorage.Instance.Next();
            DisplayQueryHistory(nextQuery);
        }

        private void DisplayQueryHistory(HistoryItem history)
        {
            if (history != null)
            {
                ChangeQueryText(history.Query, true);
                pnlResult.Dirty = true;
                var executeQueryHistoryTitle = GetTranslation("executeQuery");
                var lastExecuteTime = GetTranslation("lastExecuteTime");
                UpdateResultViewInternal(new List<Result>()
                {
                    new Result(){
                        Title = string.Format(executeQueryHistoryTitle,history.Query),
                        SubTitle = string.Format(lastExecuteTime,history.ExecutedDateTime),
                        IcoPath = "Images\\history.png",
                        PluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        Action = _ =>{
                            ChangeQuery(history.Query,true);
                            return false;
                        }
                    }
                });
            }
        }

        private void SelectItem(int index)
        {
            int zeroBasedIndex = index - 1;
            SpecialKeyState keyState = GlobalHotkey.Instance.CheckModifiers();
            if (keyState.AltPressed || keyState.CtrlPressed)
            {
                List<Result> visibleResults = pnlResult.GetVisibleResults();
                if (zeroBasedIndex < visibleResults.Count)
                {
                    SelectResult(visibleResults[zeroBasedIndex]);
                }
            }
        }

        private bool IsInContextMenuMode
        {
            get { return pnlContextMenu.Visibility == Visibility.Visible; }
        }

        private Result GetActiveResult()
        {
            if (IsInContextMenuMode)
            {
                return pnlContextMenu.GetActiveResult();
            }
            else
            {
                return pnlResult.GetActiveResult();
            }
        }

        private void SelectPrevItem()
        {
            if (IsInContextMenuMode)
            {
                pnlContextMenu.SelectPrev();
            }
            else
            {
                pnlResult.SelectPrev();
            }
            toolTip.IsOpen = false;
        }

        private void SelectNextItem()
        {
            if (IsInContextMenuMode)
            {
                pnlContextMenu.SelectNext();
            }
            else
            {
                pnlResult.SelectNext();
            }
            toolTip.IsOpen = false;
        }

        private void SelectResult(Result result)
        {
            if (result != null)
            {
                if (result.Action != null)
                {
                    bool hideWindow = result.Action(new ActionContext()
                    {
                        SpecialKeyState = GlobalHotkey.Instance.CheckModifiers()
                    });
                    if (hideWindow)
                    {
                        HideBloop();
                    }
                    UserSelectedRecordStorage.Instance.Add(result);
                    QueryHistoryStorage.Instance.Add(tbQuery.Text);
                }
            }
        }

        private void UpdateResultView(List<Result> list)
        {
            queryHasReturn = true;
            progressBar.Dispatcher.Invoke(new Action(StopProgress));
            if (list == null || list.Count == 0) return;

            if (list.Count > 0)
            {
                list.ForEach(o =>
                {
                    o.Score += UserSelectedRecordStorage.Instance.GetSelectedCount(o) * 5;
                });
                List<Result> l = list.Where(o => o.OriginQuery != null && o.OriginQuery.RawQuery == lastQuery).ToList();
                UpdateResultViewInternal(l);
            }
        }

        private void UpdateResultViewInternal(List<Result> list)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                pnlResult.AddResults(list);
            }));
        }

        private Result GetTopMostContextMenu(Result result)
        {
            if (TopMostRecordStorage.Instance.IsTopMost(result))
            {
                return new Result(GetTranslation("cancelTopMostInThisQuery"), "Images\\down.png")
                {
                    PluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    Action = _ =>
                    {
                        TopMostRecordStorage.Instance.Remove(result);
                        ShowMsg("Succeed", "", "");
                        return false;
                    }
                };
            }
            else
            {
                return new Result(GetTranslation("setAsTopMostInThisQuery"), "Images\\up.png")
                {
                    PluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    Action = _ =>
                    {
                        TopMostRecordStorage.Instance.AddOrUpdate(result);
                        ShowMsg("Succeed", "", "");
                        return false;
                    }
                };
            }
        }

        private void ShowContextMenu(Result result)
        {
            List<Result> results = PluginManager.GetPluginContextMenus(result);
            results.ForEach(o =>
            {
                o.PluginDirectory = PluginManager.GetPlugin(result.PluginID).Metadata.PluginDirectory;
                o.PluginID = result.PluginID;
                o.OriginQuery = result.OriginQuery;
            });

            results.Add(GetTopMostContextMenu(result));

            textBeforeEnterContextMenuMode = tbQuery.Text;
            ChangeQueryText("");
            pnlContextMenu.Clear();
            pnlContextMenu.AddResults(results);
            CurrentContextMenus = results;
            pnlContextMenu.Visibility = Visibility.Visible;
            pnlResult.Visibility = Visibility.Collapsed;
        }

        public bool ShellRun(string cmd, bool runAsAdministrator = false)
        {
            try
            {
                if (string.IsNullOrEmpty(cmd))
                    throw new ArgumentNullException();

                WindowsShellRun.Start(cmd, runAsAdministrator);
                return true;
            }
            catch (Exception ex)
            {
                string errorMsg = string.Format(InternationalizationManager.Instance.GetTranslation("couldnotStartCmd"), cmd);
                ShowMsg(errorMsg, ex.Message, null);
            }
            return false;
        }

        private void MainWindow_OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files[0].ToLower().EndsWith(".Bloop"))
                {
                    PluginManager.InstallPlugin(files[0]);
                }
                else
                {
                    MessageBox.Show(InternationalizationManager.Instance.GetTranslation("invalidBloopPluginFileFormat"));
                }
            }
        }

        private void TbQuery_OnPreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }
    }
}