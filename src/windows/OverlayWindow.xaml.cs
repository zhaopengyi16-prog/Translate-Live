using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.Utils;
using Color = System.Windows.Media.Color;
using ColorEnum = LiveCaptionsTranslator.Utils.Color;

namespace LiveCaptionsTranslator
{
    public partial class OverlayWindow : Window
    {
        private readonly Dictionary<ColorEnum, SolidColorBrush> colorMap = new ()
        {
            {ColorEnum.White, Brushes.White},
            {ColorEnum.Yellow, Brushes.Yellow},
            {ColorEnum.LimeGreen, Brushes.LimeGreen},
            {ColorEnum.Aqua, Brushes.Aqua},
            {ColorEnum.Blue, Brushes.Blue},
            {ColorEnum.DeepPink, Brushes.DeepPink},
            {ColorEnum.Red, Brushes.Red},
            {ColorEnum.Black, Brushes.Black},
        };
        private CaptionVisible onlyMode = CaptionVisible.Both;
        private int controlPanelAnimationVersion;

        public CaptionVisible OnlyMode
        {
            get => onlyMode;
            set
            {
                onlyMode = value;
                ResizeForOnlyMode();
            }
        }
        public CaptionLocation SwitchMode { get; set; } = CaptionLocation.TranslationTop;

        public OverlayWindow()
        {
            InitializeComponent();
            DataContext = Translator.Caption;

            Loaded += (s, e) => Translator.Caption.PropertyChanged += TranslatedChanged;
            Unloaded += (s, e) => Translator.Caption.PropertyChanged -= TranslatedChanged;

            OriginalCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold == Utils.FontBold.Both ?
                FontWeights.Bold : FontWeights.Regular;
            TranslatedCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold >= Utils.FontBold.TranslationOnly ?
                FontWeights.Bold : FontWeights.Regular;

            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;

            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);

            GlassTintLayer.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];
            ControlPanelTintLayer.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];

            ApplyFontSize();
            ApplyBackgroundOpacity();
            UpdateOnlyModeButtonContent();
            UpdateEmptyState();
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void TopThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height - e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Top += e.VerticalChange;
                this.Height = newHeight;
            }
        }

        private void BottomThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height + e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Height = newHeight;
            }
        }

        private void LeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Left += e.HorizontalChange;
                this.Width = newWidth;
            }
        }

        private void RightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Width = newWidth;
            }
        }

        private void TopLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void TopRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void BottomLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void BottomRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void TranslatedChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(models.Caption.OverlayOriginalCaption)
                or nameof(models.Caption.OverlayNoticePrefix)
                or nameof(models.Caption.OverlayCurrentTranslation)
                or nameof(models.Caption.OverlayPreviousTranslation))
            {
                Dispatcher.BeginInvoke(new Action(UpdateEmptyState), DispatcherPriority.Render);
            }
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            SetControlPanelVisible(true);
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            SetControlPanelVisible(false);
        }

        private void FontIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize + StyleConsts.DELTA_FONT_SIZE < StyleConsts.MAX_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize += StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize - StyleConsts.DELTA_FONT_SIZE > StyleConsts.MIN_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize -= StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontBold_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontBold++;
            if (Translator.Setting.OverlayWindow.FontBold > Utils.FontBold.Both)
                Translator.Setting.OverlayWindow.FontBold = Utils.FontBold.None;
            switch (Translator.Setting.OverlayWindow.FontBold)
            {
                case Utils.FontBold.None:
                    OriginalCaption.FontWeight = FontWeights.Regular;
                    TranslatedCaption.FontWeight = FontWeights.Regular;
                    break;
                case Utils.FontBold.TranslationOnly:
                    OriginalCaption.FontWeight = FontWeights.Regular;
                    TranslatedCaption.FontWeight = FontWeights.Bold;
                    break;
                case Utils.FontBold.SubtitleOnly:
                    OriginalCaption.FontWeight = FontWeights.Bold;
                    TranslatedCaption.FontWeight = FontWeights.Regular;
                    break;
                case Utils.FontBold.Both:
                    OriginalCaption.FontWeight = FontWeights.Bold;
                    TranslatedCaption.FontWeight = FontWeights.Bold;
                    break;
            }
        }

        private void FontStrokeIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke + StyleConsts.DELTA_STROKE > StyleConsts.MAX_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke += StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontStrokeDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke - StyleConsts.DELTA_STROKE < StyleConsts.MIN_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke -= StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontColor++;
            if (Translator.Setting.OverlayWindow.FontColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.FontColor = ColorEnum.White;
            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            TranslatedCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);
        }

        private void BackgroundOpacityIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity + StyleConsts.DELTA_OPACITY < StyleConsts.MAX_OPACITY)
                Translator.Setting.OverlayWindow.Opacity += StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MAX_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundOpacityDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity - StyleConsts.DELTA_OPACITY > StyleConsts.MIN_OPACITY)
                Translator.Setting.OverlayWindow.Opacity -= StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MIN_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.BackgroundColor++;
            if (Translator.Setting.OverlayWindow.BackgroundColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.BackgroundColor = ColorEnum.White;
            GlassTintLayer.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];
            ControlPanelTintLayer.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];

            ApplyBackgroundOpacity();
        }

        private void OnlyModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // (0) Subtitle + Translation
                OnlyMode = CaptionVisible.Both;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // (1) Translation Only
                OnlyMode = CaptionVisible.TranslationOnly;
            }
            else
            {
                // (2) Subtitle Only
                OnlyMode = CaptionVisible.SubtitleOnly;
            }
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (SwitchMode == CaptionLocation.TranslationTop)
            {
                Grid.SetRow(TranslatedCaptionCard, 1);
                Grid.SetRow(OriginalCaptionCard, 0);
                SwitchMode = CaptionLocation.SubtitleTop;
            }
            else
            {
                Grid.SetRow(TranslatedCaptionCard, 0);
                Grid.SetRow(OriginalCaptionCard, 1);
                SwitchMode = CaptionLocation.TranslationTop;
            }
        }

        private void ClickThrough_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = WindowsAPI.GetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE);
            WindowsAPI.SetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE, extendedStyle | WindowsAPI.WS_EX_TRANSPARENT);
            SetControlPanelVisible(false, animate: false);
        }

        public void ResizeForOnlyMode()
        {
            if (onlyMode == CaptionVisible.TranslationOnly)
            {
                // (1) Translation Only
                OriginalCaptionCard.Visibility = Visibility.Collapsed;
                this.MinHeight -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Top += StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // restore
                OriginalCaptionCard.Visibility = Visibility.Visible;
                this.Top -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height += StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.MinHeight += StyleConsts.DELTA_OVERLAY_HEIGHT;

                // (2) Subtitle Only
                TranslatedCaptionCard.Visibility = Visibility.Collapsed;
                this.MinHeight -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height -= StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // restore
                TranslatedCaptionCard.Visibility = Visibility.Visible;
                this.Height += StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.MinHeight += StyleConsts.DELTA_OVERLAY_HEIGHT;
            }

            UpdateOnlyModeButtonContent();
            UpdateEmptyState();
        }

        public void ApplyFontSize()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OriginalCaption.FontSize = Translator.Setting.OverlayWindow.FontSize;
                TranslatedCaption.FontSize = (int)(OriginalCaption.FontSize * 1.25);
            }), DispatcherPriority.Background);
        }

        public void ApplyFontStroke()
        {
            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
        }

        public void ApplyBackgroundOpacity()
        {
            Color color = ((SolidColorBrush)GlassTintLayer.Background).Color;
            Color tint = Color.FromArgb(
                (byte)Translator.Setting.OverlayWindow.Opacity, color.R, color.G, color.B);
            GlassTintLayer.Background = new SolidColorBrush(tint);
            ControlPanelTintLayer.Background = new SolidColorBrush(tint);
        }

        private void UpdateTranslationColor(SolidColorBrush brush)
        {
            var color = brush.Color;
            
            double target = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 127 ? 0 : 255;
            byte r = (byte)Math.Clamp(color.R + (target - color.R) * 0.3, 0, 255);
            byte g = (byte)Math.Clamp(color.G + (target - color.G) * 0.4, 0, 255);
            byte b = (byte)Math.Clamp(color.B + (target - color.B) * 0.3, 0, 255);

            NoticePrefixRun.Foreground = brush;
            PreviousTranslationRun.Foreground = brush;
            CurrentTranslationRun.Foreground = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void UpdateEmptyState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateEmptyState), DispatcherPriority.Render);
                return;
            }

            var caption = Translator.Caption;
            bool hasVisibleContent = caption != null &&
                OverlayCaptionPresentation.HasVisibleContent(
                onlyMode,
                caption.OverlayOriginalCaption,
                caption.OverlayNoticePrefix,
                caption.OverlayPreviousTranslation,
                caption.OverlayCurrentTranslation);
            EmptyStatePanel.Visibility = hasVisibleContent
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void SetControlPanelVisible(bool visible, bool animate = true)
        {
            int animationVersion = Interlocked.Increment(ref controlPanelAnimationVersion);
            bool reduceMotion = Translator.Setting?.Appearance.ReduceMotion == true || !animate;
            var translate = ControlPanel.RenderTransform as TranslateTransform;

            ControlPanel.BeginAnimation(OpacityProperty, null);
            translate?.BeginAnimation(TranslateTransform.YProperty, null);

            if (visible)
            {
                ControlPanel.Visibility = Visibility.Visible;
                if (reduceMotion)
                {
                    ControlPanel.Opacity = 1;
                    if (translate != null)
                        translate.Y = 0;
                    return;
                }

                double currentOpacity = ControlPanel.Opacity;
                ControlPanel.Opacity = 1;
                ControlPanel.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(currentOpacity, 1, TimeSpan.FromMilliseconds(170))
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseOut
                        }
                    });
                if (translate != null)
                {
                    double currentOffset = translate.Y;
                    translate.Y = 0;
                    translate.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(currentOffset, 0, TimeSpan.FromMilliseconds(170))
                        {
                            EasingFunction = new QuadraticEase
                            {
                                EasingMode = EasingMode.EaseOut
                            }
                        });
                }
                return;
            }

            if (reduceMotion || ControlPanel.Visibility != Visibility.Visible)
            {
                ControlPanel.Opacity = 0;
                ControlPanel.Visibility = Visibility.Collapsed;
                if (translate != null)
                    translate.Y = 4;
                return;
            }

            double opacity = ControlPanel.Opacity;
            ControlPanel.Opacity = 0;
            var fade = new DoubleAnimation(opacity, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            };
            fade.Completed += (_, _) =>
            {
                if (animationVersion == Volatile.Read(ref controlPanelAnimationVersion))
                    ControlPanel.Visibility = Visibility.Collapsed;
            };
            ControlPanel.BeginAnimation(OpacityProperty, fade);

            if (translate != null)
            {
                double currentOffset = translate.Y;
                translate.Y = 4;
                translate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(currentOffset, 4, TimeSpan.FromMilliseconds(130))
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseIn
                        }
                    });
            }
        }

        private void UpdateOnlyModeButtonContent()
        {
            string label = onlyMode switch
            {
                CaptionVisible.TranslationOnly => "仅译",
                CaptionVisible.SubtitleOnly => "仅原",
                _ => "双语"
            };
            OnlyModeButton.Content = label;
            OnlyModeButton.ToolTip = $"当前显示：{label}；点击切换";
        }
    }
}
