using System.Windows.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CH32UpperComputer.App.Views
{
    /// <summary>
    /// 承载 CKAPP 六要素监控、报警信息和手动数据收发区域。
    /// </summary>
    public partial class MonitorView : UserControl
    {
        /// <summary>
        /// 单张监测卡片按原型从轻微下沉和缩小状态落定的时长。
        /// </summary>
        private static readonly TimeSpan SensorEntryDuration =
            TimeSpan.FromMilliseconds(480);

        /// <summary>
        /// 鼠标悬停时监测卡片轻微上浮或归位的时长。
        /// </summary>
        private static readonly TimeSpan SensorHoverDuration =
            TimeSpan.FromMilliseconds(260);

        /// <summary>
        /// 六张卡片的节奏差；同一视觉斜线上的卡片共享延迟。
        /// </summary>
        private static readonly int[] SensorEntryDelays = [0, 35, 70, 35, 70, 105];

        /// <summary>
        /// 初始化监控页 XAML 组件；复杂数据卡直接呈现，避免加载阶段触发大面积重绘。
        /// </summary>
        public MonitorView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 鼠标进入监测卡时播放两像素上浮反馈，帮助确认当前可交互区域。
        /// </summary>
        /// <param name="sender">当前鼠标进入的监测卡 Border。</param>
        /// <param name="eventArgs">本次鼠标进入事件参数。</param>
        private void HandleSensorCardMouseEnter(
            object sender,
            MouseEventArgs eventArgs)
        {
            _ = eventArgs;

            if (sender is Border sensorCard)
            {
                AnimateSensorCardHover(sensorCard, -2d);
            }
        }

        /// <summary>
        /// 鼠标离开监测卡时把卡片平滑恢复到稳定位置。
        /// </summary>
        /// <param name="sender">当前鼠标离开的监测卡 Border。</param>
        /// <param name="eventArgs">本次鼠标离开事件参数。</param>
        private void HandleSensorCardMouseLeave(
            object sender,
            MouseEventArgs eventArgs)
        {
            _ = eventArgs;

            if (sender is Border sensorCard)
            {
                AnimateSensorCardHover(sensorCard, 0d);
            }
        }

        /// <summary>
        /// 从卡片当前屏幕位置接续到悬停目标，快速往返时不会先跳回旧起点。
        /// </summary>
        /// <param name="sensorCard">需要改变合成层纵向位置的监测卡 Border。</param>
        /// <param name="targetY">悬停时为 -2，归位时为 0。</param>
        private static void AnimateSensorCardHover(
            Border sensorCard,
            double targetY)
        {
            TranslateTransform translation = GetMutableSensorCardTranslation(sensorCard);
            double currentY = translation.Y;
            translation.BeginAnimation(TranslateTransform.YProperty, null);
            translation.Y = targetY;

            if (!SystemParameters.ClientAreaAnimation ||
                !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CH32_CAPTURE_PATH")) ||
                Math.Abs(currentY - targetY) < 0.05d)
            {
                return;
            }

            translation.BeginAnimation(
                TranslateTransform.YProperty,
                CreateSensorSplineAnimation(
                    currentY,
                    targetY,
                    SensorHoverDuration,
                    TimeSpan.Zero),
                HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>
        /// 获取卡片的可动画纵向位移对象；若 XAML 模板将共享对象冻结，则克隆为当前卡片独享的可写实例。
        /// </summary>
        /// <param name="sensorCard">需要执行悬停位移动画的监测卡 Border。</param>
        /// <returns>已挂载到卡片且未冻结的 TranslateTransform。</returns>
        private static TranslateTransform GetMutableSensorCardTranslation(
            Border sensorCard)
        {
            if (sensorCard.RenderTransform is TranslateTransform existingTranslation)
            {
                if (!existingTranslation.IsFrozen)
                {
                    return existingTranslation;
                }

                TranslateTransform mutableTranslation =
                    existingTranslation.CloneCurrentValue();
                sensorCard.RenderTransform = mutableTranslation;
                return mutableTranslation;
            }

            TranslateTransform createdTranslation = new();
            sensorCard.RenderTransform = createdTranslation;
            return createdTranslation;
        }

        /// <summary>
        /// 为当前已生成的六张监测卡播放与最终 HTML 原型一致的短波次入场。
        /// </summary>
        internal void PlaySensorEntryMotion()
        {
            ResetSensorEntryMotion();

            if (!SystemParameters.ClientAreaAnimation ||
                !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CH32_CAPTURE_PATH")))
            {
                return;
            }

            SensorCardsItemsControl.UpdateLayout();

            for (int cardIndex = 0;
                 cardIndex < SensorCardsItemsControl.Items.Count;
                 cardIndex++)
            {
                if (SensorCardsItemsControl.ItemContainerGenerator.ContainerFromIndex(
                    cardIndex) is not FrameworkElement cardContainer)
                {
                    continue;
                }

                TimeSpan delay = TimeSpan.FromMilliseconds(
                    cardIndex < SensorEntryDelays.Length
                        ? SensorEntryDelays[cardIndex]
                        : 0);
                (ScaleTransform scale, TranslateTransform translation) =
                    GetOrCreateSensorTransforms(cardContainer);
                cardContainer.Opacity = 1d;
                scale.ScaleX = 1d;
                scale.ScaleY = 1d;
                translation.Y = 0d;
                cardContainer.BeginAnimation(
                    UIElement.OpacityProperty,
                    CreateSensorSplineAnimation(0d, 1d, SensorEntryDuration, delay),
                    HandoffBehavior.SnapshotAndReplace);
                scale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    CreateSensorSplineAnimation(0.99d, 1d, SensorEntryDuration, delay),
                    HandoffBehavior.SnapshotAndReplace);
                scale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    CreateSensorSplineAnimation(0.99d, 1d, SensorEntryDuration, delay),
                    HandoffBehavior.SnapshotAndReplace);
                translation.BeginAnimation(
                    TranslateTransform.YProperty,
                    CreateSensorSplineAnimation(8d, 0d, SensorEntryDuration, delay),
                    HandoffBehavior.SnapshotAndReplace);
            }
        }

        /// <summary>
        /// 移除所有监测卡片的旧动画时钟并恢复稳定终态，防止快速离开再返回时残留半帧。
        /// </summary>
        internal void ResetSensorEntryMotion()
        {
            SensorCardsItemsControl.UpdateLayout();

            for (int cardIndex = 0;
                 cardIndex < SensorCardsItemsControl.Items.Count;
                 cardIndex++)
            {
                if (SensorCardsItemsControl.ItemContainerGenerator.ContainerFromIndex(
                    cardIndex) is not FrameworkElement cardContainer)
                {
                    continue;
                }

                (ScaleTransform scale, TranslateTransform translation) =
                    GetOrCreateSensorTransforms(cardContainer);
                cardContainer.BeginAnimation(UIElement.OpacityProperty, null);
                cardContainer.Opacity = 1d;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1d;
                scale.ScaleY = 1d;
                translation.BeginAnimation(TranslateTransform.YProperty, null);
                translation.Y = 0d;
            }
        }

        /// <summary>
        /// 获取卡片容器专用的缩放和平移变换，并无损保留可能存在的既有变换。
        /// </summary>
        /// <param name="cardContainer">需要执行合成层入场动画的 ItemsControl 项容器。</param>
        /// <returns>可分别控制缩放和纵向位移的变换对象。</returns>
        private static (ScaleTransform Scale, TranslateTransform Translation)
            GetOrCreateSensorTransforms(FrameworkElement cardContainer)
        {
            if (cardContainer.RenderTransform is TransformGroup existingGroup)
            {
                ScaleTransform? existingScale =
                    existingGroup.Children.OfType<ScaleTransform>().FirstOrDefault();
                TranslateTransform? existingTranslation =
                    existingGroup.Children.OfType<TranslateTransform>().FirstOrDefault();

                if (existingScale is not null && existingTranslation is not null)
                {
                    return (existingScale, existingTranslation);
                }
            }

            TransformGroup transformGroup = new();

            if (cardContainer.RenderTransform is not null &&
                cardContainer.RenderTransform != Transform.Identity)
            {
                transformGroup.Children.Add(cardContainer.RenderTransform);
            }

            ScaleTransform scale = new(1d, 1d);
            TranslateTransform translation = new(0d, 0d);
            transformGroup.Children.Add(scale);
            transformGroup.Children.Add(translation);
            cardContainer.RenderTransform = transformGroup;
            cardContainer.RenderTransformOrigin = new Point(0.5d, 0.5d);
            return (scale, translation);
        }

        /// <summary>
        /// 创建监测卡专用的 cubic-bezier(.22, 1, .36, 1) 样条动画。
        /// </summary>
        /// <param name="from">动画第一帧的属性值。</param>
        /// <param name="to">卡片稳定落定后的属性值。</param>
        /// <param name="duration">本次卡片属性变化的持续时间。</param>
        /// <param name="delay">当前动画相对触发时刻的延迟。</param>
        /// <returns>仅改变合成属性、不触发布局重排的关键帧动画。</returns>
        private static DoubleAnimationUsingKeyFrames CreateSensorSplineAnimation(
            double from,
            double to,
            TimeSpan duration,
            TimeSpan delay)
        {
            DoubleAnimationUsingKeyFrames animation = new()
            {
                BeginTime = delay,
                Duration = duration,
                FillBehavior = FillBehavior.Stop,
            };
            animation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(
                    from,
                    KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(
                new SplineDoubleKeyFrame(
                    to,
                    KeyTime.FromTimeSpan(duration),
                    new KeySpline(0.22d, 1d, 0.36d, 1d)));
            return animation;
        }
    }
}
