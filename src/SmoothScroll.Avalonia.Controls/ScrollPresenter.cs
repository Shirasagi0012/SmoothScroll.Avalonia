using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using PropertyGenerator.Avalonia;
using SmoothScroll.Avalonia.Interaction;
using Vector = Avalonia.Vector;

namespace SmoothScroll.Avalonia.Controls;

/// <summary>
/// Provides the shared smooth scrolling engine and the default physical scrolling semantics used by <see cref="ScrollView"/>.
/// </summary>
public partial class ScrollPresenter : ContentPresenter, IScrollable, IScrollAnchorProvider, IInteractionTrackerOwner
{
    private const double EdgeDetectionTolerance = 0.1;
    private const int ArrangeTimerIntervalMs = 40;
    private const int ArrangeTimerIdleTimeoutMs = 160;
    private const int BringIntoViewAnimationDurationMilliseconds = 500;

    /// <summary>
    /// Defines the <see cref="CanHorizontallyScroll"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> CanHorizontallyScrollProperty =
        AvaloniaProperty.Register<ScrollPresenter, bool>(nameof(CanHorizontallyScroll));

    /// <summary>
    /// Defines the <see cref="CanVerticallyScroll"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> CanVerticallyScrollProperty =
        AvaloniaProperty.Register<ScrollPresenter, bool>(nameof(CanVerticallyScroll));

    /// <summary>
    /// Defines the <see cref="ComputedHorizontalScrollMode"/> property.
    /// </summary>
    public static readonly DirectProperty<ScrollPresenter, ScrollMode> ComputedHorizontalScrollModeProperty =
        AvaloniaProperty.RegisterDirect<ScrollPresenter, ScrollMode>(
            nameof(ComputedHorizontalScrollMode),
            presenter => presenter.ComputedHorizontalScrollMode);

    /// <summary>
    /// Defines the <see cref="ComputedVerticalScrollMode"/> property.
    /// </summary>
    public static readonly DirectProperty<ScrollPresenter, ScrollMode> ComputedVerticalScrollModeProperty =
        AvaloniaProperty.RegisterDirect<ScrollPresenter, ScrollMode>(
            nameof(ComputedVerticalScrollMode),
            presenter => presenter.ComputedVerticalScrollMode);

    /// <summary>
    /// Defines the <see cref="Extent"/> property.
    /// </summary>
    public static readonly DirectProperty<ScrollPresenter, Size> ExtentProperty =
        ScrollViewer.ExtentProperty.AddOwner<ScrollPresenter>(o => o.Extent);

    /// <summary>
    /// Defines the <see cref="Offset"/> property.
    /// </summary>
    public static readonly StyledProperty<Vector> OffsetProperty =
        ScrollViewer.OffsetProperty.AddOwner<ScrollPresenter>(new(coerce: ScrollViewer.CoerceOffset));

    /// <summary>
    /// Defines the <see cref="Viewport"/> property.
    /// </summary>
    public static readonly DirectProperty<ScrollPresenter, Size> ViewportProperty =
        ScrollViewer.ViewportProperty.AddOwner<ScrollPresenter>(o => o.Viewport);

    /// <summary>
    /// Defines the <see cref="HorizontalSnapPointsType"/> property.
    /// </summary>
    public static readonly StyledProperty<SnapPointsType> HorizontalSnapPointsTypeProperty =
        ScrollViewer.HorizontalSnapPointsTypeProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="VerticalSnapPointsType"/> property.
    /// </summary>
    public static readonly StyledProperty<SnapPointsType> VerticalSnapPointsTypeProperty =
        ScrollViewer.VerticalSnapPointsTypeProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="HorizontalSnapPointsAlignment"/> property.
    /// </summary>
    public static readonly StyledProperty<SnapPointsAlignment> HorizontalSnapPointsAlignmentProperty =
        ScrollViewer.HorizontalSnapPointsAlignmentProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="VerticalSnapPointsAlignment"/> property.
    /// </summary>
    public static readonly StyledProperty<SnapPointsAlignment> VerticalSnapPointsAlignmentProperty =
        ScrollViewer.VerticalSnapPointsAlignmentProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="IsScrollChainingEnabled"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsScrollChainingEnabledProperty =
        ScrollViewer.IsScrollChainingEnabledProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="HorizontalAnchorRatio"/> property.
    /// </summary>
    public static readonly StyledProperty<double> HorizontalAnchorRatioProperty =
        ScrollView.HorizontalAnchorRatioProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="VerticalAnchorRatio"/> property.
    /// </summary>
    public static readonly StyledProperty<double> VerticalAnchorRatioProperty =
        ScrollView.VerticalAnchorRatioProperty.AddOwner<ScrollPresenter>();

    /// <summary>
    /// Defines the <see cref="GestureBindings"/> property.
    /// </summary>
    public static readonly StyledProperty<ScrollGestureBindings> GestureBindingsProperty =
        AvaloniaProperty.Register<ScrollPresenter, ScrollGestureBindings>(nameof(GestureBindings));

    public static readonly StyledProperty<double> ScrollInputMultiplierProperty =
        AvaloniaProperty.Register<ScrollPresenter, double>(nameof(ScrollInputMultiplier), 1);

    public static readonly StyledProperty<double> ZoomInputMultiplierProperty =
        AvaloniaProperty.Register<ScrollPresenter, double>(nameof(ZoomInputMultiplier), 1);

    public static readonly StyledProperty<double> ScrollInertiaDecayRateProperty =
        AvaloniaProperty.Register<ScrollPresenter, double>(
            nameof(ScrollInertiaDecayRate),
            ScrollPhysicsDefaults.LegacyWheelAndZoomInertiaDecayRate);

    public static readonly StyledProperty<double> ZoomInertiaDecayRateProperty =
        AvaloniaProperty.Register<ScrollPresenter, double>(
            nameof(ZoomInertiaDecayRate),
            ScrollPhysicsDefaults.LegacyWheelAndZoomInertiaDecayRate);

    public static readonly StyledProperty<double> OverscrollElasticityProperty =
        AvaloniaProperty.Register<ScrollPresenter, double>(nameof(OverscrollElasticity), 0.5);

    public static readonly StyledProperty<double> OverscrollBounceRateProperty =
        AvaloniaProperty.Register<ScrollPresenter, double>(nameof(OverscrollBounceRate), 1);

    /// <summary>
    /// Defines the <see cref="IsBringIntoViewAnimationEnabled"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsBringIntoViewAnimationEnabledProperty =
        AvaloniaProperty.Register<ScrollPresenter, bool>(nameof(IsBringIntoViewAnimationEnabled), true);

    public event EventHandler<ScrollAnimationStartingEventArgs>? ScrollAnimationStarting;

    private InteractionTracker? _interactionTracker;
    private InputElementInteractionSource? _interactionSource;
    private CompositionAnimationGroup? _animationGroup;
    private bool _compositionUpdate;
    private bool _scaleChanged;
    private int? _requestId;
    private int? _scrollRequestId;
    private int? _zoomRequestId;
    private int _scrollCorrelationId = ScrollView.NoCorrelationId;
    private int _zoomCorrelationId = ScrollView.NoCorrelationId;
    private bool _scrollRequestAnimated;
    private bool _zoomRequestAnimated;
    private bool _arranging;
    private HashSet<Control>? _anchorCandidates;
    private Rect _anchorElementBounds;
    private bool _isAnchorElementDirty;
    private bool _areVerticalSnapPointsRegular;
    private bool _areHorizontalSnapPointsRegular;
    private IReadOnlyList<double>? _horizontalSnapPoints;
    private double _horizontalSnapPoint;
    private IReadOnlyList<double>? _verticalSnapPoints;
    private double _verticalSnapPoint;
    private double _verticalSnapPointOffset;
    private double _horizontalSnapPointOffset;
    private IScrollSnapPointsInfo? _scrollSnapPointsInfo;
    private bool _isSnapPointsUpdated;
    private InteractionTrackerInertiaStateEnteredArgs? _inertiaArgs;
    private readonly DispatcherTimer _arrangeTimer;
    private long _lastScrollActivityTick;
    private ScrollView? _scrollViewOwner;
    private Vector _trackerPosition;
    private ScrollChangeSource _changeSource = ScrollChangeSource.Layout;
    private ScrollMode _computedHorizontalScrollMode = ScrollMode.Disabled;
    private ScrollMode _computedVerticalScrollMode = ScrollMode.Disabled;
    private ScrollingInteractionState _interactionState;
    private VerticalScrollAttachment? _verticalScrollAttachment;
    private bool _presenterLoaded;


    /// <summary>
    /// Gets whether content scrolling is delegated to an <see cref="ILogicalScrollable"/> implementation.
    /// </summary>
    protected virtual bool IsLogicalScrollActive => false;

    private bool HasActiveTrackerRequest => _scrollRequestId is not null || _zoomRequestId is not null;

    internal bool HasPendingArrange { get; private set; }

    /// <summary>
    /// Initializes static members of the <see cref="ScrollPresenter"/> class.
    /// </summary>
    static ScrollPresenter()
    {
        ClipToBoundsProperty.OverrideDefaultValue(typeof(ScrollPresenter), true);
        AffectsMeasure<ScrollPresenter>(
            CanHorizontallyScrollProperty,
            CanVerticallyScrollProperty,
            IsZoomEnabledProperty,
            IsHorizontalMeasureInfiniteProperty,
            IsVerticalMeasureInfiniteProperty,
            HorizontalScrollModeProperty,
            VerticalScrollModeProperty);
        AffectsArrange<ScrollPresenter>(HorizontalAnchorRatioProperty, VerticalAnchorRatioProperty);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ScrollPresenter"/> class.
    /// </summary>
    public ScrollPresenter()
    {
        SetCurrentValue(GestureBindingsProperty, ScrollGestureBindings.CreateDefault());
        _arrangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ArrangeTimerIntervalMs), };
        _arrangeTimer.Tick += ArrangeTimerTick;
        AddHandler(RequestBringIntoViewEvent, BringIntoViewRequested);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the content can be scrolled horizontally.
    /// </summary>
    public bool CanHorizontallyScroll
    {
        get => GetValue(CanHorizontallyScrollProperty);
        set => SetValue(CanHorizontallyScrollProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the content can be scrolled horizontally.
    /// </summary>
    public bool CanVerticallyScroll
    {
        get => GetValue(CanVerticallyScrollProperty);
        set => SetValue(CanVerticallyScrollProperty, value);
    }

    /// <summary>
    /// Gets or sets whether standard bring-into-view requests without an explicit animation preference are animated.
    /// </summary>
    public bool IsBringIntoViewAnimationEnabled
    {
        get => GetValue(IsBringIntoViewAnimationEnabledProperty);
        set => SetValue(IsBringIntoViewAnimationEnabledProperty, value);
    }

    /// <summary>
    /// Gets the effective horizontal user-input mode.
    /// </summary>
    /// <remarks>This property is always <see cref="ScrollMode.Enabled"/> or <see cref="ScrollMode.Disabled"/>.</remarks>
    public ScrollMode ComputedHorizontalScrollMode => _computedHorizontalScrollMode;

    /// <summary>
    /// Gets the effective vertical user-input mode.
    /// </summary>
    /// <remarks>This property is always <see cref="ScrollMode.Enabled"/> or <see cref="ScrollMode.Disabled"/>.</remarks>
    public ScrollMode ComputedVerticalScrollMode => _computedVerticalScrollMode;

    /// <summary>
    /// Gets the extent of the scrollable content.
    /// </summary>
    public Size Extent
    {
        get;
        private set => SetAndRaise(ExtentProperty, ref field, value);
    }

    /// <summary>
    /// Gets or sets the current scroll offset.
    /// </summary>
    public Vector Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    /// <summary>
    /// Gets the size of the viewport on the scrollable content.
    /// </summary>
    public Size Viewport
    {
        get;
        private set => SetAndRaise(ViewportProperty, ref field, value);
    }

    /// <summary>
    /// Gets or sets how scroll gesture reacts to the snap points along the horizontal axis.
    /// </summary>
    public SnapPointsType HorizontalSnapPointsType
    {
        get => GetValue(HorizontalSnapPointsTypeProperty);
        set => SetValue(HorizontalSnapPointsTypeProperty, value);
    }

    /// <summary>
    /// Gets or sets how scroll gesture reacts to the snap points along the vertical axis.
    /// </summary>
    public SnapPointsType VerticalSnapPointsType
    {
        get => GetValue(VerticalSnapPointsTypeProperty);
        set => SetValue(VerticalSnapPointsTypeProperty, value);
    }

    /// <summary>
    /// Gets or sets how the existing snap points are horizontally aligned versus the initial viewport.
    /// </summary>
    public SnapPointsAlignment HorizontalSnapPointsAlignment
    {
        get => GetValue(HorizontalSnapPointsAlignmentProperty);
        set => SetValue(HorizontalSnapPointsAlignmentProperty, value);
    }

    /// <summary>
    /// Gets or sets how the existing snap points are vertically aligned versus the initial viewport.
    /// </summary>
    public SnapPointsAlignment VerticalSnapPointsAlignment
    {
        get => GetValue(VerticalSnapPointsAlignmentProperty);
        set => SetValue(VerticalSnapPointsAlignmentProperty, value);
    }

    /// <summary>
    ///  Gets or sets if scroll chaining is enabled. The default value is true.
    /// </summary>
    /// <remarks>
    ///  After a user hits a scroll limit on an element that has been nested within another scrollable element,
    /// you can specify whether that parent element should continue the scrolling operation begun in its child element.
    /// This is called scroll chaining.
    /// </remarks>
    public bool IsScrollChainingEnabled
    {
        get => GetValue(IsScrollChainingEnabledProperty);
        set => SetValue(IsScrollChainingEnabledProperty, value);
    }

    /// <summary>
    /// Gets or sets the unique gesture-and-modifier mappings used for pan, scroll, and zoom input.
    /// </summary>
    public ScrollGestureBindings GestureBindings
    {
        get => GetValue(GestureBindingsProperty);
        set => SetValue(GestureBindingsProperty, value);
    }

    public double ScrollInputMultiplier
    {
        get => GetValue(ScrollInputMultiplierProperty);
        set => SetValue(ScrollInputMultiplierProperty, value);
    }

    public double ZoomInputMultiplier
    {
        get => GetValue(ZoomInputMultiplierProperty);
        set => SetValue(ZoomInputMultiplierProperty, value);
    }

    public double ScrollInertiaDecayRate
    {
        get => GetValue(ScrollInertiaDecayRateProperty);
        set => SetValue(ScrollInertiaDecayRateProperty, value);
    }

    public double ZoomInertiaDecayRate
    {
        get => GetValue(ZoomInertiaDecayRateProperty);
        set => SetValue(ZoomInertiaDecayRateProperty, value);
    }

    public double OverscrollElasticity
    {
        get => GetValue(OverscrollElasticityProperty);
        set => SetValue(OverscrollElasticityProperty, value);
    }

    public double OverscrollBounceRate
    {
        get => GetValue(OverscrollBounceRateProperty);
        set => SetValue(OverscrollBounceRateProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum zoom factor.
    /// </summary>
    [GeneratedStyledProperty(0.1)]
    public partial double MinZoomFactor { get; set; }

    /// <summary>
    /// Gets or sets the maximum zoom factor.
    /// </summary>
    [GeneratedStyledProperty(10)]
    public partial double MaxZoomFactor { get; set; }

    /// <summary>
    /// Gets or sets the current zoom factor.
    /// </summary>
    [GeneratedStyledProperty(1)]
    public partial double ZoomFactor { get; set; }

    /// <summary>
    /// Gets or sets whether zooming is enabled.
    /// </summary>
    [GeneratedStyledProperty]
    public partial bool IsZoomEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the child is measured with an infinite horizontal constraint.
    /// </summary>
    [GeneratedStyledProperty]
    public partial bool IsHorizontalMeasureInfinite { get; set; }

    /// <summary>
    /// Gets or sets whether the child is measured with an infinite vertical constraint.
    /// </summary>
    [GeneratedStyledProperty(true)]
    public partial bool IsVerticalMeasureInfinite { get; set; }

    /// <summary>
    /// Gets or sets a value that determines how manipulation input influences scrolling behavior on the horizontal axis.
    /// </summary>
    [GeneratedStyledProperty(ScrollMode.Auto)]
    public partial ScrollMode HorizontalScrollMode { get; set; }

    /// <summary>
    /// Gets or sets a value that determines how manipulation input influences scrolling behavior on the vertical axis.
    /// </summary>
    [GeneratedStyledProperty(ScrollMode.Auto)]
    public partial ScrollMode VerticalScrollMode { get; set; }

    /// <summary>
    /// Gets or sets the horizontal viewport ratio used for automatic anchor selection.
    /// </summary>
    /// <remarks>
    /// Values are coerced to <c>[0, 1]</c>. Zero represents the left edge, one represents the right edge,
    /// and <see cref="double.NaN"/> disables horizontal anchoring.
    /// </remarks>
    public double HorizontalAnchorRatio
    {
        get => GetValue(HorizontalAnchorRatioProperty);
        set => SetValue(HorizontalAnchorRatioProperty, value);
    }

    /// <summary>
    /// Gets or sets the vertical viewport ratio used for automatic anchor selection.
    /// </summary>
    /// <remarks>
    /// Values are coerced to <c>[0, 1]</c>. Zero represents the top edge, one represents the bottom edge,
    /// and <see cref="double.NaN"/> disables vertical anchoring.
    /// </remarks>
    public double VerticalAnchorRatio
    {
        get => GetValue(VerticalAnchorRatioProperty);
        set => SetValue(VerticalAnchorRatioProperty, value);
    }

    /// <summary>
    /// Gets the anchor candidate selected by the most recently completed layout pass.
    /// </summary>
    public Control? CurrentAnchor { get; private set; }

    Control? IScrollAnchorProvider.CurrentAnchor => CurrentAnchor;

    /// <summary>Whether the presenter is using physical content offsets rather than logical item offsets.</summary>
    public bool IsPhysicalScrollActive => !IsLogicalScrollActive;

    /// <summary>
    /// Registers a visual descendant as an automatic anchor candidate.
    /// </summary>
    /// <param name="element">The descendant to register.</param>
    public void RegisterAnchorCandidate(Control element) =>
        ((IScrollAnchorProvider)this).RegisterAnchorCandidate(element);

    /// <summary>
    /// Removes a previously registered automatic anchor candidate.
    /// </summary>
    /// <param name="element">The candidate to remove.</param>
    public void UnregisterAnchorCandidate(Control element) =>
        ((IScrollAnchorProvider)this).UnregisterAnchorCandidate(element);

    /// <summary>
    /// Attempts to bring a portion of the target visual into view by scrolling the content.
    /// </summary>
    /// <param name="target">The target visual.</param>
    /// <param name="targetRect">The portion of the target visual to bring into view.</param>
    /// <returns>True if the scroll offset was changed; otherwise false.</returns>
    public bool BringDescendantIntoView(Visual target, Rect targetRect) =>
        BringDescendantIntoView(target, targetRect, IsBringIntoViewAnimationEnabled);

    /// <summary>
    /// Attempts to bring a portion of a descendant visual into view by scrolling the content.
    /// </summary>
    /// <param name="target">The target visual.</param>
    /// <param name="targetRect">The portion of the target visual to bring into view.</param>
    /// <param name="isAnimated">Whether the scroll transition should be animated.</param>
    /// <returns><see langword="true"/> when the target requires a scroll request; otherwise <see langword="false"/>.</returns>
    public bool BringDescendantIntoView(Visual target, Rect targetRect, bool isAnimated)
    {
        if (Child is not { IsEffectivelyVisible: true } child
            || (!ReferenceEquals(target, child) && !child.IsVisualAncestorOf(target)))
            return false;

        if (IsLogicalScrollActive && target is Control targetControl)
            return BringLogicalDescendantIntoView(targetControl, targetRect, isAnimated);

        var transform = target.TransformToVisual(child);
        if (transform is null)
            return false;

        var rectangle = TransformTargetRectangle(
            targetRect.TransformToAABB(transform.Value),
            child.Margin + Padding,
            ZoomFactor);
        var viewport = new Rect(Offset.X, Offset.Y, Viewport.Width, Viewport.Height);

        var minX = ComputeScrollOffsetWithMinimalScroll(viewport.Left, viewport.Right, rectangle.Left, rectangle.Right);
        var minY = ComputeScrollOffsetWithMinimalScroll(viewport.Top, viewport.Bottom, rectangle.Top, rectangle.Bottom);
        var endPosition = ClampOffsetToEnabledAxes(new Vector(minX, minY));
        if (Offset.NearlyEquals(endPosition))
            return false;

        var startingPosition = Offset;
        if (!isAnimated || _interactionTracker is null || GetCompositionVisual() is not { } visual)
        {
            _ = ScrollTo(endPosition, isAnimated: false, ScrollChangeSource.Programmatic);
            return true;
        }

        if (_interactionState is ScrollingInteractionState.Interaction)
            return false;

        InterruptOperationsForTrackerRequest();
        var correlationId = BeginScrollOperation(
            startingPosition,
            endPosition,
            isAnimated: true,
            ScrollChangeSource.Programmatic);
        if (_scrollViewOwner is { } owner && !owner.IsScrollOperationActive(correlationId))
            return true;

        var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(BringIntoViewAnimationDurationMilliseconds);
        var trackerEndPosition = ToTrackerPosition(endPosition, CalculateScrollableArea(ZoomFactor));
        animation.InsertKeyFrame(1f, new Vector3D(trackerEndPosition.X, trackerEndPosition.Y, 0), new CircularEaseOut());
        var args = new ScrollAnimationStartingEventArgs(animation, startingPosition, endPosition);
        ScrollAnimationStarting?.Invoke(this, args);
        if (_scrollViewOwner is { } currentOwner && !currentOwner.IsScrollOperationActive(correlationId))
            return true;

        _changeSource = ScrollChangeSource.Programmatic;
        _scrollRequestId = _interactionTracker.TryUpdatePositionWithAnimation(args.Animation);
        _requestId = _scrollRequestId;
        return true;
    }

    /// <summary>
    /// Brings a descendant into view using a logical scrolling implementation.
    /// </summary>
    protected virtual bool BringLogicalDescendantIntoView(Control target, Rect targetRect, bool isAnimated) => false;

    private static Rect TransformTargetRectangle(Rect rectangle, Thickness contentMargin, double scale) =>
        new(
            (rectangle.X + contentMargin.Left) * scale,
            (rectangle.Y + contentMargin.Top) * scale,
            rectangle.Width * scale,
            rectangle.Height * scale);

    /// <summary>
    /// Computes the closest offset to ensure most of the child is visible in the viewport along an axis.
    /// </summary>
    /// <param name="viewportStart">The left or top of the viewport</param>
    /// <param name="viewportEnd">The right or bottom of the viewport</param>
    /// <param name="childStart">The left or top of the child</param>
    /// <param name="childEnd">The right or bottom of the child</param>
    /// <returns></returns>
    internal static double ComputeScrollOffsetWithMinimalScroll(
        double viewportStart,
        double viewportEnd,
        double childStart,
        double childEnd)
    {
        // If child is at least partially above viewport, i.e. top of child is above viewport top and bottom of child is above viewport bottom.
        bool isChildAbove = MathUtilities.LessThan(childStart, viewportStart) && MathUtilities.LessThan(childEnd, viewportEnd);

        // If child is at least partially below viewport, i.e. top of child is below viewport top and bottom of child is below viewport bottom.
        bool isChildBelow = MathUtilities.GreaterThan(childEnd, viewportEnd) && MathUtilities.GreaterThan(childStart, viewportStart);
        bool isChildLarger = (childEnd - childStart) > (viewportEnd - viewportStart);

        // Value if no updates is needed. The child is fully visible in the viewport, or the viewport is completely within the child's bounds
        var res = viewportStart;

        // The child is above the viewport and is smaller than the viewport, or if the child's top is below the viewport top
        // and is larger than the viewport, we align the child top to the top of the viewport
        if ((isChildAbove && !isChildLarger)
            || (isChildBelow && isChildLarger))
        {
            res = childStart;
        }
        // The child is above the viewport and is larger than the viewport, or if the child's smaller but is below the viewport,
        // we align the child's bottom to the bottom of the viewport
        else if (isChildAbove || isChildBelow)
        {
            res = (childEnd - (viewportEnd - viewportStart));
        }

        return res;
    }


    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _presenterLoaded = false;
        UpdateVerticalScrollAttachment();
        var compositionVisual = GetCompositionVisual();
        // Reattach from the last published viewport, not an unacknowledged request on the detached tracker.
        DisposeInteractionTracker();
        base.OnDetachedFromVisualTree(e);
        StopArrangeTimer();
        ClearScrollAnimation(compositionVisual);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        _presenterLoaded = true;
        Initialize();
        UpdateVerticalScrollAttachment();
        base.OnLoaded(e);
    }

    private void Initialize()
    {
        InitializeInteractionTracker();
        EnsureScrollAnimation();
    }

    private void InitializeInteractionTracker()
    {
        var compositionVisual = GetCompositionVisual();
        if (compositionVisual is null)
        {
            return;
        }

        if (_interactionTracker is not null)
        {
            if (_interactionTracker.Compositor != compositionVisual.Compositor)
            {
                DisposeInteractionTracker();
            }
            else
            {
                _interactionTracker.MinScale = MinZoomFactor;
                _interactionTracker.MaxScale = MaxZoomFactor;
                UpdateInteractionOptions();
                return;
            }
        }

        var scrollableArea = CalculateScrollableArea(ZoomFactor);
        var logicalPosition = ToTrackerPosition(Offset, scrollableArea);
        var initialPosition = new Vector3D(logicalPosition.X, logicalPosition.Y, 0);
        _trackerPosition = logicalPosition;

        _interactionTracker = compositionVisual!.Compositor.CreateInteractionTracker(this);
        _interactionTracker.Position = initialPosition;
        _interactionTracker.Scale = ZoomFactor;
        _interactionTracker.MinScale = MinZoomFactor;
        _interactionTracker.MaxScale = MaxZoomFactor;
        _interactionSource = new InputElementInteractionSource(this, _interactionTracker);

        try
        {
            _compositionUpdate = true;
            _scaleChanged = !MathUtilities.AreClose(ZoomFactor, 1.0);
        }
        finally
        {
            _compositionUpdate = false;
            _scaleChanged = false;
        }

        UpdateInteractionOptions();
    }

    private void DisposeInteractionTracker()
    {
        _verticalScrollAttachment?.Detach();
        InterruptOperations();
        SetInteractionState(ScrollingInteractionState.Idle);
        _interactionSource?.Dispose();
        _interactionSource = null;
        _interactionTracker?.Dispose();
        _interactionTracker = null;
        _requestId = null;
    }

    internal void AttachToScrollView(ScrollView owner)
    {
        if (ReferenceEquals(_scrollViewOwner, owner))
            return;

        _scrollViewOwner = owner;
        owner.UpdateInteractionState(_interactionState);
        UpdateOwnerConfiguration(owner);
        SetCurrentValue(OffsetProperty, owner.Offset);
    }

    internal void DetachFromScrollView(ScrollView owner)
    {
        if (ReferenceEquals(_scrollViewOwner, owner))
        {
            InterruptOperations();
            owner.UpdateInteractionState(ScrollingInteractionState.Idle);
            _scrollViewOwner = null;
        }
    }

    internal void UpdateOwnerConfiguration(ScrollView owner)
    {
        if (!ReferenceEquals(_scrollViewOwner, owner))
            return;

        SetCurrentValue(MinZoomFactorProperty, owner.MinZoomFactor);
        SetCurrentValue(MaxZoomFactorProperty, owner.MaxZoomFactor);
        SetCurrentValue(ZoomFactorProperty, owner.ZoomFactor);
        SetCurrentValue(IsZoomEnabledProperty, owner.IsZoomEnabled);
        SetCurrentValue(IsHorizontalMeasureInfiniteProperty, owner.IsHorizontalMeasureInfinite);
        SetCurrentValue(IsVerticalMeasureInfiniteProperty, owner.IsVerticalMeasureInfinite);
        SetCurrentValue(HorizontalScrollModeProperty, owner.HorizontalScrollMode);
        SetCurrentValue(VerticalScrollModeProperty, owner.VerticalScrollMode);
        SetCurrentValue(HorizontalAnchorRatioProperty, owner.HorizontalAnchorRatio);
        SetCurrentValue(VerticalAnchorRatioProperty, owner.VerticalAnchorRatio);
        SetCurrentValue(IsScrollChainingEnabledProperty, owner.IsScrollChainingEnabled);
        SetCurrentValue(ScrollViewer.IsScrollInertiaEnabledProperty, owner.IsScrollInertiaEnabled);
        SetCurrentValue(HorizontalSnapPointsTypeProperty, owner.HorizontalSnapPointsType);
        SetCurrentValue(VerticalSnapPointsTypeProperty, owner.VerticalSnapPointsType);
        SetCurrentValue(HorizontalSnapPointsAlignmentProperty, owner.HorizontalSnapPointsAlignment);
        SetCurrentValue(VerticalSnapPointsAlignmentProperty, owner.VerticalSnapPointsAlignment);
        SetCurrentValue(GestureBindingsProperty, owner.GestureBindings);
        SetCurrentValue(ScrollInputMultiplierProperty, owner.ScrollInputMultiplier);
        SetCurrentValue(ZoomInputMultiplierProperty, owner.ZoomInputMultiplier);
        SetCurrentValue(ScrollInertiaDecayRateProperty, owner.ScrollInertiaDecayRate);
        SetCurrentValue(ZoomInertiaDecayRateProperty, owner.ZoomInertiaDecayRate);
        SetCurrentValue(OverscrollElasticityProperty, owner.OverscrollElasticity);
        SetCurrentValue(OverscrollBounceRateProperty, owner.OverscrollBounceRate);
        UpdateInteractionOptions();
    }

    internal int SetOffsetFromOwner(Vector startingOffset, Vector offset, ScrollChangeSource source)
    {
        offset = ClampOffsetToEnabledAxes(offset);
        if (Offset.NearlyEquals(offset) || _interactionState is ScrollingInteractionState.Interaction)
            return ScrollView.NoCorrelationId;

        var completeOnIdle = HasActiveTrackerRequest
                             || _interactionState is ScrollingInteractionState.Inertia or ScrollingInteractionState.Animation;
        InterruptOperationsForTrackerRequest();
        var correlationId = BeginScrollOperation(
            startingOffset,
            offset,
            isAnimated: false,
            source,
            completeOnIdle);
        if (_scrollViewOwner is { } owner && !owner.IsScrollOperationActive(correlationId))
            return correlationId;

        _changeSource = source;
        SetCurrentValue(OffsetProperty, offset);
        _scrollRequestId = _requestId;
        if (_scrollRequestId is null)
        {
            SynchronizeScrollViewOwner(source);
            Dispatcher.UIThread.Post(
                () => CompleteScrollOperation(ScrollingOperationResult.Completed),
                DispatcherPriority.Render);
        }

        return correlationId;
    }

    /// <inheritdoc/>
    void IScrollAnchorProvider.RegisterAnchorCandidate(Control element)
    {
        if (!this.IsVisualAncestorOf(element))
        {
            throw new InvalidOperationException(
                "An anchor control must be a visual descendent of the ScrollContentPresenter.");
        }

        _anchorCandidates ??= new();
        _ = _anchorCandidates.Add(element);
        _isAnchorElementDirty = true;
        InvalidateArrange();
    }

    /// <inheritdoc/>
    void IScrollAnchorProvider.UnregisterAnchorCandidate(Control element)
    {
        _ = (_anchorCandidates?.Remove(element));
        _isAnchorElementDirty = true;
        InvalidateArrange();

        if (CurrentAnchor == element)
        {
            CurrentAnchor = null;
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null)
        {
            return base.MeasureOverride(availableSize);
        }

        if (IsLogicalScrollActive)
        {
            var result = base.MeasureOverride(availableSize);
            SynchronizeLogicalScrollState();
            return result;
        }

        var availableWithPadding = availableSize.Deflate(Padding);
        var constraint = new Size(
            IsHorizontalMeasureInfinite ? double.PositiveInfinity : availableWithPadding.Width,
            IsVerticalMeasureInfinite ? double.PositiveInfinity : availableWithPadding.Height);

        Child.Measure(constraint);

        if (!_isSnapPointsUpdated)
        {
            _isSnapPointsUpdated = true;
            UpdateSnapPoints();
        }

        return Child.DesiredSize.Inflate(Padding).Constrain(availableSize);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child == null)
        {
            return base.ArrangeOverride(finalSize);
        }

        if (IsLogicalScrollActive)
        {
            var result = base.ArrangeOverride(finalSize);
            SynchronizeLogicalScrollState();
            return result;
        }

        return ArrangeWithAnchoring(finalSize);
    }

    private Size ArrangeWithAnchoring(Size finalSize)
    {
        _arranging = true;
        try
        {
            double width = 0;
            double height = 0;
            UpdateComputedScrollMode(finalSize, useArrangedSize: false);
            if (IsZoomEnabled)
            {
                width = (HorizontalContentAlignment == HorizontalAlignment.Stretch) ?
                    Math.Max(Child!.DesiredSize.Inflate(Padding).Width, finalSize.Width) :
                    finalSize.Width;
                height = (VerticalContentAlignment == VerticalAlignment.Stretch) ?
                    Math.Max(Child!.DesiredSize.Inflate(Padding).Height, finalSize.Height) :
                    finalSize.Height;
            }
            else
            {
                width = CanHorizontallyScroll ? Math.Max(Child!.DesiredSize.Inflate(Padding).Width, finalSize.Width) : finalSize.Width;
                height = CanVerticallyScroll ? Math.Max(Child!.DesiredSize.Inflate(Padding).Height, finalSize.Height) : finalSize.Height;
            }

            var size = new Size(width, height);
            var anchoring = GetAnchoringState();
            if (anchoring.ElementHorizontal || anchoring.ElementVertical)
                EnsureAnchorElementSelection(anchoring.ElementHorizontal, anchoring.ElementVertical);
            else
                ResetAnchorElement();

            _ = ArrangeOverrideImpl(size, GetArrangeOffset());
            var anchoredOffset = Offset + TrackAnchor(anchoring.ElementHorizontal, anchoring.ElementVertical);

            UpdateComputedScrollMode(finalSize, useArrangedSize: true);
            Viewport = finalSize;
            _isAnchorElementDirty = true;

            var scrollableArea = CalculateScrollableArea(ZoomFactor);
            if (_interactionTracker is null)
                Initialize();
            ApplyScrollableArea(scrollableArea);

            var targetOffset = Offset;
            if (anchoring.ElementHorizontal)
                targetOffset = targetOffset.WithX(anchoredOffset.X);
            else if (anchoring.FarHorizontal)
                targetOffset = targetOffset.WithX(scrollableArea.MaxPosition.X - scrollableArea.MinPosition.X);

            if (anchoring.ElementVertical)
                targetOffset = targetOffset.WithY(anchoredOffset.Y);
            else if (anchoring.FarVertical)
                targetOffset = targetOffset.WithY(scrollableArea.MaxPosition.Y - scrollableArea.MinPosition.Y);

            if (!Offset.NearlyEquals(targetOffset))
            {
                ApplyAnchoredOffset(targetOffset, scrollableArea);
                _ = ArrangeOverrideImpl(size, GetArrangeOffset());
            }

            SynchronizeCompositionVisualBeforeFirstAnimation();
            SynchronizeScrollViewOwner(ScrollChangeSource.Layout);
        }
        finally
        {
            _arranging = false;
        }

        return finalSize;
    }

    private (bool ElementHorizontal, bool ElementVertical, bool FarHorizontal, bool FarVertical) GetAnchoringState()
    {
        var horizontalMaximum = Math.Max(Extent.Width - Viewport.Width, 0);
        var verticalMaximum = Math.Max(Extent.Height - Viewport.Height, 0);
        var horizontalRatio = HorizontalAnchorRatio;
        var verticalRatio = VerticalAnchorRatio;
        var farHorizontal = horizontalRatio == 1 && Offset.X >= horizontalMaximum - EdgeDetectionTolerance;
        var farVertical = verticalRatio == 1 && Offset.Y >= verticalMaximum - EdgeDetectionTolerance;
        var elementHorizontal = !double.IsNaN(horizontalRatio)
                                && !farHorizontal
                                && !(horizontalRatio == 0 && Offset.X <= EdgeDetectionTolerance);
        var elementVertical = !double.IsNaN(verticalRatio)
                              && !farVertical
                              && !(verticalRatio == 0 && Offset.Y <= EdgeDetectionTolerance);
        return (elementHorizontal, elementVertical, farHorizontal, farVertical);
    }

    private void ApplyAnchoredOffset(
        Vector offset,
        (Size Extent, Size ScaledExtent, Vector MinPosition, Vector MaxPosition) scrollableArea)
    {
        try
        {
            _compositionUpdate = true;
            SetCurrentValue(OffsetProperty, offset);

            if (_interactionTracker is not null)
            {
                var trackerPosition = ToTrackerPosition(Offset, scrollableArea);
                _trackerPosition = trackerPosition;
                _changeSource = ScrollChangeSource.Layout;
                _requestId = _interactionTracker.TryUpdatePosition(
                    new Vector3D(trackerPosition.X, trackerPosition.Y, 0));
            }
        }
        finally
        {
            _compositionUpdate = false;
        }
    }

    private void RequestArrangeOnScroll()
    {
        HasPendingArrange = true;
        _lastScrollActivityTick = Environment.TickCount64;

        if (!_arrangeTimer.IsEnabled)
        {
            _arrangeTimer.Start();
        }
    }

    private void ArrangeTimerTick(object? sender, EventArgs e)
    {
        if (HasPendingArrange)
        {
            HasPendingArrange = false;
            InvalidateArrange();
        }

        if (Environment.TickCount64 - _lastScrollActivityTick >= ArrangeTimerIdleTimeoutMs)
        {
            StopArrangeTimer();
        }
    }

    private void StopArrangeTimer()
    {
        HasPendingArrange = false;

        if (_arrangeTimer.IsEnabled)
        {
            _arrangeTimer.Stop();
        }
    }

    private void UpdateComputedScrollMode(Size finalSize, bool useArrangedSize)
    {
        var childSize = useArrangedSize ? Child!.Bounds.Size : Child!.DesiredSize;
        var contentSize = useArrangedSize ? childSize.Inflate(Child.Margin + Padding) : childSize.Inflate(Padding);
        var scale = IsZoomEnabled ? ZoomFactor : 1;
        var scaledContentSize = new Size(contentSize.Width * scale, contentSize.Height * scale);
        UpdateCanScroll(
            scaledContentSize.Width > finalSize.Width,
            scaledContentSize.Height > finalSize.Height);
    }

    /// <summary>
    /// Updates axis scrolling state from the current content overflow.
    /// </summary>
    protected virtual void UpdateCanScroll(bool horizontalOverflow, bool verticalOverflow)
    {
        SetCurrentValue(
            CanHorizontallyScrollProperty,
            CanScrollHorizontally(horizontalOverflow));
        SetCurrentValue(
            CanVerticallyScrollProperty,
            CanScrollVertically(verticalOverflow));
        UpdateComputedScrollModeProperties();
    }

    /// <summary>
    /// Updates the public computed scroll modes from the current axis state.
    /// </summary>
    protected void UpdateComputedScrollModeProperties()
    {
        var horizontalMode = CanHorizontallyScroll ? ScrollMode.Enabled : ScrollMode.Disabled;
        var verticalMode = CanVerticallyScroll ? ScrollMode.Enabled : ScrollMode.Disabled;
        _ = SetAndRaise(ComputedHorizontalScrollModeProperty, ref _computedHorizontalScrollMode, horizontalMode);
        _ = SetAndRaise(ComputedVerticalScrollModeProperty, ref _computedVerticalScrollMode, verticalMode);
    }

    partial void OnPropertyChangedOverride(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == OffsetProperty)
        {
            if (!_arranging && !_scaleChanged)
            {
                if (_compositionUpdate)
                {
                    RequestArrangeOnScroll();
                }
                else
                {
                    InvalidateArrange();
                }
            }

            if (!_scaleChanged && !_compositionUpdate && _interactionTracker is not null)
            {
                var offset = change.GetNewValue<Vector>();
                var trackerPosition = ToTrackerPosition(offset, CalculateScrollableArea(ZoomFactor));
                _trackerPosition = trackerPosition;
                _requestId = _interactionTracker.TryUpdatePosition(new Vector3D(trackerPosition.X, trackerPosition.Y, 0));
            }
            else
            {
                _requestId = null;
            }

            OnOffsetChanged(change.GetNewValue<Vector>());
        }
        else if (change.Property == ChildProperty)
        {
            ChildChanged(change);
        }
        else if (change.Property == HorizontalSnapPointsAlignmentProperty ||
                 change.Property == VerticalSnapPointsAlignmentProperty)
        {
            UpdateSnapPoints();
        }
        else if (change.Property == ExtentProperty)
        {
            var previousCompositionUpdate = _compositionUpdate;
            try
            {
                // Live participant bounds own geometry correction. Native mirrors can temporarily
                // have older viewport data; do not send their coercion back as absolute input.
                _compositionUpdate |= _verticalScrollAttachment?.IsAttached == true;
                OnExtentChanged(change.GetNewValue<Size>());
                if (!_scaleChanged)
                    CoerceValue(OffsetProperty);
            }
            finally { _compositionUpdate = previousCompositionUpdate; }
        }
        else if (change.Property == ViewportProperty)
        {
            var previousCompositionUpdate = _compositionUpdate;
            try
            {
                _compositionUpdate |= _verticalScrollAttachment?.IsAttached == true;
                OnViewportChanged(change.GetNewValue<Size>());
                CoerceValue(OffsetProperty);
            }
            finally { _compositionUpdate = previousCompositionUpdate; }
        }
        else if (change.Property == PaddingProperty)
        {
            ClearScrollAnimation(GetCompositionVisual());
            EnsureScrollAnimation();
        }
        else if (change.Property == CanVerticallyScrollProperty ||
                 change.Property == CanHorizontallyScrollProperty)
        {
            UpdateComputedScrollModeProperties();
            UpdateInteractionOptions();
        }
        else if (change.Property == IsZoomEnabledProperty)
        {
            OnIsZoomEnabledChanged();
            UpdateInteractionOptions();
        }
        else if (change.Property == ScrollViewer.IsScrollInertiaEnabledProperty ||
                 change.Property == GestureBindingsProperty ||
                 change.Property == ScrollInputMultiplierProperty ||
                 change.Property == ZoomInputMultiplierProperty ||
                 change.Property == ScrollInertiaDecayRateProperty ||
                 change.Property == ZoomInertiaDecayRateProperty ||
                 change.Property == OverscrollElasticityProperty ||
                 change.Property == OverscrollBounceRateProperty)
            UpdateInteractionOptions();
        else if (change.Property == HorizontalAnchorRatioProperty ||
                 change.Property == VerticalAnchorRatioProperty)
        {
            _isAnchorElementDirty = true;
        }
        else if (change.Property == ZoomFactorProperty)
        {
            if (!_compositionUpdate && _interactionTracker is not null)
            {
                var scale = change.GetNewValue<double>();
                _ = ZoomTo(scale);
            }
        }
        else if (change.Property == MinZoomFactorProperty)
        {
            _interactionTracker?.MinScale = change.GetNewValue<double>();
        }
        else if (change.Property == MaxZoomFactorProperty)
        {
            _interactionTracker?.MaxScale = change.GetNewValue<double>();
        }

        base.OnPropertyChanged(change);
    }

    private void ScrollSnapPointsInfoSnapPointsChanged(object? sender, RoutedEventArgs e)
    {
        UpdateSnapPoints();
    }

    private void BringIntoViewRequested(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not null)
        {
            var isAnimated = e is SmoothScrollBringIntoViewRequestEventArgs request ? request.IsAnimated : IsBringIntoViewAnimationEnabled;
            e.Handled = BringDescendantIntoView(e.TargetObject, e.TargetRect, isAnimated);
        }
    }

    private void ChildChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is Control oldChild)
        {
            oldChild.AttachedToVisualTree -= ChildAttachedToVisualTree;
            ClearScrollAnimation(ElementComposition.GetElementVisual(oldChild));
        }
        else
        {
            ClearScrollAnimation(GetCompositionVisual());
        }

        if (e.OldValue is not null)
        {
            SetCurrentValue(OffsetProperty, default);
            var compositionVisual = ElementComposition.GetElementVisual((e.OldValue as Control)!);
            compositionVisual?.ImplicitAnimations = null;
        }

        OnChildChanged(e.NewValue as Control);
        if (e.NewValue is Control newChild)
            newChild.AttachedToVisualTree += ChildAttachedToVisualTree;
        EnsureScrollAnimation();
    }

    private void ChildAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Cached pages can render before Loaded runs. Restore the composition transform as soon
        // as its child visual is available, so the first frame uses the retained tracker state.
        Initialize();
    }

    /// <summary>
    /// Called after the presented child changes.
    /// </summary>
    protected virtual void OnChildChanged(Control? child)
    {
    }

    /// <summary>
    /// Called after <see cref="IsZoomEnabled"/> changes.
    /// </summary>
    protected virtual void OnIsZoomEnabledChanged()
    {
    }

    /// <summary>
    /// Called after the logical content offset changes.
    /// </summary>
    protected virtual void OnOffsetChanged(Vector offset)
    {
    }

    /// <summary>
    /// Called after the presenter extent changes.
    /// </summary>
    protected virtual void OnExtentChanged(Size extent)
    {
    }

    /// <summary>
    /// Called after the presenter viewport changes.
    /// </summary>
    protected virtual void OnViewportChanged(Size viewport)
    {
    }

    /// <summary>
    /// Synchronizes state supplied by an active logical scrolling implementation.
    /// </summary>
    protected virtual void SynchronizeLogicalScrollState()
    {
    }

    /// <summary>
    /// Applies viewport and offset state supplied by a logical scrolling implementation.
    /// </summary>
    protected void ApplyLogicalScrollState(Size viewport, Vector offset)
    {
        Viewport = viewport;
        ApplyScrollableArea(CalculateScrollableArea(ZoomFactor));
        SetCurrentValue(OffsetProperty, offset);
    }

    /// <summary>
    /// Updates composition animation state after logical scrolling is enabled or disabled.
    /// </summary>
    protected void UpdateLogicalScrollMode(bool wasLogicalScrollActive)
    {
        if (wasLogicalScrollActive == IsLogicalScrollActive)
            return;

        UpdateVerticalScrollAttachment();

        var compositionVisual = GetCompositionVisual();
        ClearScrollAnimation(compositionVisual);
        if (IsLogicalScrollActive)
        {
            if (compositionVisual is not null)
            {
                compositionVisual.Translation = default;
                compositionVisual.Scale = new Vector3D(1, 1, 1);
            }

            return;
        }

        EnsureScrollAnimation();
    }

    private void EnsureAnchorElementSelection(bool horizontal, bool vertical)
    {
        if (!_isAnchorElementDirty)
            return;

        ResetAnchorElement();
        if (_anchorCandidates is null)
            return;

        var candidates = new List<(Control Element, Rect Bounds)>();
        foreach (var element in _anchorCandidates)
        {
            if (element.IsVisible && GetViewportBounds(element, out var bounds))
                candidates.Add((element, bounds));
        }

        if (candidates.Count == 0)
            return;

        var requestedElement = _scrollViewOwner?
            .RaiseAnchorRequested(candidates.Select(candidate => candidate.Element))?
            .AnchorElement;
        if (requestedElement is not null &&
            candidates.Any(candidate => ReferenceEquals(candidate.Element, requestedElement)) &&
            _anchorCandidates.Contains(requestedElement) &&
            requestedElement.IsVisible &&
            GetViewportBounds(requestedElement, out _) &&
            TranslateBounds(requestedElement, Child!, out var requestedBounds))
        {
            CurrentAnchor = requestedElement;
            _anchorElementBounds = requestedBounds;
            return;
        }

        var bestCandidate = default(Control);
        var bestCandidateDistance = double.MaxValue;
        var viewportAnchorX = Bounds.Width * HorizontalAnchorRatio;
        var viewportAnchorY = Bounds.Height * VerticalAnchorRatio;

        foreach (var (element, _) in candidates)
        {
            if (!_anchorCandidates.Contains(element) ||
                !element.IsVisible ||
                !GetViewportBounds(element, out var bounds))
                continue;

            var candidateDistance = 0.0;
            if (horizontal)
            {
                var elementAnchorX = bounds.X + HorizontalAnchorRatio * bounds.Width;
                var distance = viewportAnchorX - elementAnchorX;
                candidateDistance += distance * distance;
            }

            if (vertical)
            {
                var elementAnchorY = bounds.Y + VerticalAnchorRatio * bounds.Height;
                var distance = viewportAnchorY - elementAnchorY;
                candidateDistance += distance * distance;
            }

            if (candidateDistance < bestCandidateDistance)
            {
                bestCandidate = element;
                bestCandidateDistance = candidateDistance;
            }
        }

        if (bestCandidate != null)
        {
            CurrentAnchor = bestCandidate;
            _anchorElementBounds = TranslateBounds(bestCandidate, Child!);
        }
    }

    private Vector TrackAnchor(bool horizontal, bool vertical)
    {
        if (CurrentAnchor is not null &&
            TranslateBounds(CurrentAnchor, Child!, out var updatedBounds) &&
            updatedBounds != _anchorElementBounds)
        {
            var oldAnchorPoint = GetElementAnchorPoint(_anchorElementBounds, horizontal, vertical);
            var newAnchorPoint = GetElementAnchorPoint(updatedBounds, horizontal, vertical);
            var shift = newAnchorPoint - oldAnchorPoint;
            return new Vector(
                horizontal ? shift.X * ZoomFactor : 0,
                vertical ? shift.Y * ZoomFactor : 0);
        }

        return default;
    }

    private Point GetElementAnchorPoint(Rect bounds, bool horizontal, bool vertical) => new(
        horizontal ? bounds.X + HorizontalAnchorRatio * bounds.Width : 0,
        vertical ? bounds.Y + VerticalAnchorRatio * bounds.Height : 0);

    private void ResetAnchorElement()
    {
        CurrentAnchor = null;
        _anchorElementBounds = default;
        _isAnchorElementDirty = false;
    }

    private bool GetViewportBounds(Control element, out Rect bounds)
    {
        if (TranslateBounds(element, Child!, out var childBounds))
        {
            var thisBounds = new Rect(Bounds.Size);
            bounds = new Rect(
                childBounds.X * ZoomFactor - Offset.X,
                childBounds.Y * ZoomFactor - Offset.Y,
                childBounds.Width * ZoomFactor,
                childBounds.Height * ZoomFactor);
            return bounds.Intersects(thisBounds);
        }

        bounds = default;
        return false;
    }

    private Rect TranslateBounds(Control control, Control to)
    {
        if (TranslateBounds(control, to, out var bounds))
        {
            return bounds;
        }

        throw new InvalidOperationException("The control's bounds could not be translated to the requested control.");
    }

    private bool TranslateBounds(Control control, Control to, out Rect bounds)
    {
        if (!control.IsVisible)
        {
            bounds = default;
            return false;
        }

        var p = control.TranslatePoint(default, to);
        bounds = p.HasValue ? new Rect(p.Value, control.Bounds.Size) : default;
        return p.HasValue;
    }

    private void UpdateSnapPoints()
    {
        var scrollable = GetScrollSnapPointsInfo(Content);

        if (scrollable is not null)
        {
            _areVerticalSnapPointsRegular = scrollable.AreVerticalSnapPointsRegular;
            _areHorizontalSnapPointsRegular = scrollable.AreHorizontalSnapPointsRegular;

            if (!_areVerticalSnapPointsRegular)
            {
                _verticalSnapPoints = scrollable.GetIrregularSnapPoints(Orientation.Vertical, VerticalSnapPointsAlignment);
            }
            else
            {
                _verticalSnapPoints = new List<double>();
                _verticalSnapPoint = scrollable.GetRegularSnapPoints(Orientation.Vertical, VerticalSnapPointsAlignment, out _verticalSnapPointOffset);
            }

            if (!_areHorizontalSnapPointsRegular)
            {
                _horizontalSnapPoints = scrollable.GetIrregularSnapPoints(Orientation.Horizontal, HorizontalSnapPointsAlignment);
            }
            else
            {
                _horizontalSnapPoints = new List<double>();
                _horizontalSnapPoint = scrollable.GetRegularSnapPoints(Orientation.Horizontal, HorizontalSnapPointsAlignment, out _horizontalSnapPointOffset);
            }
        }
        else
        {
            _horizontalSnapPoints = new List<double>();
            _verticalSnapPoints = new List<double>();
        }

        UpdateScrollModified();
    }

    private void UpdateScrollModified()
    {
        if (_inertiaArgs is not { } args
            || _interactionTracker is null
            || HorizontalSnapPointsType is SnapPointsType.None
            && VerticalSnapPointsType is SnapPointsType.None)
        {
            return;
        }

        var scale = Math.Clamp(
            args.ModifiedRestingScale ?? args.NaturalRestingScale,
            MinZoomFactor,
            MaxZoomFactor);
        var scrollableArea = CalculateScrollableArea(scale);
        var naturalTrackerPosition = new Vector(
            args.NaturalRestingPosition.X,
            args.NaturalRestingPosition.Y);
        var naturalOffset = FromTrackerPosition(naturalTrackerPosition, scrollableArea);
        var direction = new Vector(
            args.PositionVelocityInPixelsPerSecond.X,
            args.PositionVelocityInPixelsPerSecond.Y);
        var snapOffset = SnapOffset(naturalOffset, direction, args.IsInertiaFromImpulse, scale);
        var snapTrackerPosition = ToTrackerPosition(snapOffset, scrollableArea);

        // Retarget the active inertia instead of issuing a position update, which would stop
        // the inertia state and turn every snap into an immediate jump.
        _ = _interactionTracker.TryUpdatePositionInertiaRestingValue(
            new Vector3D(snapTrackerPosition.X, snapTrackerPosition.Y, 0));
    }

    private Vector SnapOffset(
        Vector offset,
        Vector direction = default,
        bool snapToNext = false,
        double scale = 1)
    {
        var scrollable = GetScrollSnapPointsInfo(Content);

        if (scrollable is null || (VerticalSnapPointsType == SnapPointsType.None && HorizontalSnapPointsType == SnapPointsType.None))
            return offset;

        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var diff = GetAlignmentDiff();

        if (VerticalSnapPointsType is not SnapPointsType.None
            && (!snapToNext || direction.Y is not 0)
            && TryFindSnapPoint(
                _areVerticalSnapPointsRegular,
                _verticalSnapPoint,
                _verticalSnapPointOffset,
                _verticalSnapPoints,
                offset.Y + diff.Y,
                direction.Y,
                snapToNext,
                scale,
                out var verticalSnapPoint))
        {
            offset = offset.WithY(verticalSnapPoint - diff.Y);
        }

        if (HorizontalSnapPointsType is not SnapPointsType.None
            && (!snapToNext || direction.X is not 0)
            && TryFindSnapPoint(
                _areHorizontalSnapPointsRegular,
                _horizontalSnapPoint,
                _horizontalSnapPointOffset,
                _horizontalSnapPoints,
                offset.X + diff.X,
                direction.X,
                snapToNext,
                scale,
                out var horizontalSnapPoint))
        {
            offset = offset.WithX(horizontalSnapPoint - diff.X);
        }

        Vector GetAlignmentDiff()
        {
            var vector = default(Vector);

            switch (VerticalSnapPointsAlignment)
            {
                case SnapPointsAlignment.Center:
                    vector += new Vector(0, Viewport.Height / 2);
                    break;
                case SnapPointsAlignment.Far:
                    vector += new Vector(0, Viewport.Height);
                    break;
            }

            switch (HorizontalSnapPointsAlignment)
            {
                case SnapPointsAlignment.Center:
                    vector += new Vector(Viewport.Width / 2, 0);
                    break;
                case SnapPointsAlignment.Far:
                    vector += new Vector(Viewport.Width, 0);
                    break;
            }

            return vector;
        }

        return offset;
    }

    private static bool TryFindSnapPoint(
        bool regular,
        double interval,
        double regularOffset,
        IReadOnlyList<double>? irregularPoints,
        double value,
        double direction,
        bool snapToNext,
        double scale,
        out double snapPoint)
    {
        if (regular && double.IsFinite(interval) && interval > 0)
        {
            var scaledInterval = interval * scale;
            var scaledOffset = regularOffset * scale;
            var normalized = (value - scaledOffset) / scaledInterval;
            var regularIndex = (snapToNext, direction > 0) switch
            {
                (true, true) => Math.Floor(normalized) + 1,
                (true, false) => Math.Ceiling(normalized) - 1,
                _ => Math.Floor(normalized + 0.5)
            };
            snapPoint = (regularIndex * scaledInterval) + scaledOffset;
            return true;
        }

        if (irregularPoints is not { Count: > 0 })
        {
            snapPoint = default;
            return false;
        }

        var unscaledValue = value / scale;
        var point = irregularPoints.BinarySearch(unscaledValue, Comparer<double>.Default);
        int index;
        if (snapToNext)
        {
            index = (direction > 0, point >= 0) switch
            {
                (true, true) => point + 1,
                (true, false) => ~point,
                (false, true) => point - 1,
                (false, false) => ~point - 1
            };
        }
        else if (point >= 0)
        {
            index = point;
        }
        else
        {
            var next = ~point;
            var previous = Math.Max(0, next - 1);
            next = Math.Min(next, irregularPoints.Count - 1);
            index = unscaledValue - irregularPoints[previous]
                    < irregularPoints[next] - unscaledValue ?
                previous :
                next;
        }

        index = Math.Clamp(index, 0, irregularPoints.Count - 1);
        snapPoint = irregularPoints[index] * scale;
        return true;
    }

    private IScrollSnapPointsInfo? GetScrollSnapPointsInfo(object? content)
    {
        var scrollable = content;

        if (Content is ItemsControl itemsControl)
            scrollable = itemsControl.Presenter?.Panel;

        if (Content is ItemsPresenter itemsPresenter)
            scrollable = itemsPresenter.Panel;

        var snapPointsInfo = scrollable as IScrollSnapPointsInfo;

        if (snapPointsInfo != _scrollSnapPointsInfo)
        {
            if (_scrollSnapPointsInfo != null)
            {
                _scrollSnapPointsInfo.VerticalSnapPointsChanged -= ScrollSnapPointsInfoSnapPointsChanged;
                _scrollSnapPointsInfo.HorizontalSnapPointsChanged -= ScrollSnapPointsInfoSnapPointsChanged;
            }

            _scrollSnapPointsInfo = snapPointsInfo;

            if (_scrollSnapPointsInfo != null)
            {
                _scrollSnapPointsInfo.VerticalSnapPointsChanged += ScrollSnapPointsInfoSnapPointsChanged;
                _scrollSnapPointsInfo.HorizontalSnapPointsChanged += ScrollSnapPointsInfoSnapPointsChanged;
            }
        }

        return snapPointsInfo;
    }

    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
    {
        if (args.RequestId == _scrollRequestId)
            CompleteScrollOperation(ScrollingOperationResult.Ignored);
        if (args.RequestId == _zoomRequestId)
            CompleteZoomOperation(ScrollingOperationResult.Ignored);
    }

    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        var position = new Vector(args.Position.X, args.Position.Y);
        var scale = args.Scale;

        void ApplyValues()
        {
            if (_interactionTracker != sender)
            {
                return;
            }

            if (args.RequestId > 0 &&
                _requestId is { } id &&
                args.RequestId < id)
            {
                return;
            }

            try
            {
                var previousOffset = Offset;
                var previousZoomFactor = ZoomFactor;
                _compositionUpdate = true;
                _scaleChanged = !MathUtilities.AreClose(scale, ZoomFactor);

                var scrollableArea = CalculateScrollableArea(scale);
                if (_scaleChanged)
                    UpdateComputedScrollMode(scrollableArea);
                _trackerPosition = position;
                ApplyScrollableArea(scrollableArea);

                SetCurrentValue(OffsetProperty, FromTrackerPosition(position, scrollableArea));
                SetCurrentValue(ZoomFactorProperty, scale);
                if (_scaleChanged)
                {
                    // Composition updates the visible scale immediately, but virtualizing children
                    // refresh their effective viewport only during layout. Keep layout close to the
                    // composition clock without arranging on every frame.
                    RequestArrangeOnScroll();
                }

                var source = args.IsUserInitiated ? ScrollChangeSource.User : _changeSource;
                if (source is ScrollChangeSource.User)
                {
                    var zoomChanged = !MathUtilities.AreClose(previousZoomFactor, ZoomFactor);
                    var hasIndependentTranslation = !zoomChanged
                                                    || _inertiaArgs is
                                                    {
                                                        PositionVelocityInPixelsPerSecond: not { X: 0, Y: 0, Z: 0 }
                                                    };
                    if (hasIndependentTranslation && !previousOffset.NearlyEquals(Offset))
                        EnsureUserScrollOperation(previousOffset, Offset);
                    if (zoomChanged)
                        EnsureUserZoomOperation(previousZoomFactor, ZoomFactor);
                }
            }
            finally
            {
                _compositionUpdate = false;
                _scaleChanged = false;
            }

            var changeSource = args.IsUserInitiated ? ScrollChangeSource.User : _changeSource;
            SynchronizeScrollViewOwner(changeSource);

            if (args.RequestId == _scrollRequestId && !_scrollRequestAnimated)
                CompleteScrollOperation(ScrollingOperationResult.Completed);
            if (args.RequestId == _zoomRequestId && !_zoomRequestAnimated)
                CompleteZoomOperation(ScrollingOperationResult.Completed);
            if (changeSource is ScrollChangeSource.User && _interactionState is ScrollingInteractionState.Idle)
            {
                CompleteScrollOperation(ScrollingOperationResult.Completed);
                CompleteZoomOperation(ScrollingOperationResult.Completed);
            }

            if (_interactionState is ScrollingInteractionState.Idle)
                _changeSource = ScrollChangeSource.Layout;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyValues();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyValues, DispatcherPriority.Render);
        }
    }


    void IInteractionTrackerOwner.CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args)
    {
        SetInteractionState(ScrollingInteractionState.Animation);
        // The initiating ScrollTo/ZoomTo request records its source before the animation starts.
        // Do not overwrite it here, as internal user or layout requests must retain their origin.
    }

    void IInteractionTrackerOwner.IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
    {
        _inertiaArgs = null;
        SetInteractionState(ScrollingInteractionState.Idle);

        CompleteScrollOperation(
            _scrollRequestId is null || _scrollRequestId == args.RequestId ? ScrollingOperationResult.Completed : ScrollingOperationResult.Interrupted);
        CompleteZoomOperation(
            _zoomRequestId is null || _zoomRequestId == args.RequestId ? ScrollingOperationResult.Completed : ScrollingOperationResult.Interrupted);
        _changeSource = ScrollChangeSource.Layout;
        Dispatcher.UIThread.Post(InvalidateArrange);
    }

    void IInteractionTrackerOwner.InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args)
    {
        _changeSource = args.RequestId is 0 ? ScrollChangeSource.User : ScrollChangeSource.Programmatic;
        _inertiaArgs = args;
        SetInteractionState(ScrollingInteractionState.Inertia);
        EnsureScrollAnimation();
        UpdateScrollModified();
    }

    void IInteractionTrackerOwner.InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
    {
        InterruptOperations();
        _changeSource = ScrollChangeSource.User;
        _inertiaArgs = null;
        SetInteractionState(ScrollingInteractionState.Interaction);
        EnsureScrollAnimation();
    }

    private int BeginScrollOperation(
        Vector startingOffset,
        Vector targetOffset,
        bool isAnimated,
        ScrollChangeSource source,
        bool completeOnIdle = false)
    {
        CompleteScrollOperation(ScrollingOperationResult.Interrupted);
        if (_scrollViewOwner is not { } owner)
            return ScrollView.NoCorrelationId;

        var correlationId = owner.BeginScrollOperation(
            startingOffset,
            targetOffset,
            isAnimated,
            source);
        if (!owner.IsScrollOperationActive(correlationId))
            return correlationId;

        _scrollCorrelationId = correlationId;
        _scrollRequestAnimated = isAnimated || completeOnIdle;
        return correlationId;
    }

    private int BeginZoomOperation(
        double startingZoomFactor,
        double targetZoomFactor,
        Point? centerPoint,
        bool isAnimated,
        ScrollChangeSource source,
        bool completeOnIdle = false)
    {
        CompleteZoomOperation(ScrollingOperationResult.Interrupted);
        if (_scrollViewOwner is not { } owner)
            return ScrollView.NoCorrelationId;

        var correlationId = owner.BeginZoomOperation(
            startingZoomFactor,
            targetZoomFactor,
            centerPoint,
            isAnimated,
            source);
        if (!owner.IsZoomOperationActive(correlationId))
            return correlationId;

        _zoomCorrelationId = correlationId;
        _zoomRequestAnimated = isAnimated || completeOnIdle;
        return correlationId;
    }

    private void EnsureUserScrollOperation(Vector startingOffset, Vector targetOffset)
    {
        if (_scrollCorrelationId is not ScrollView.NoCorrelationId)
            return;

        _ = BeginScrollOperation(
            startingOffset,
            targetOffset,
            isAnimated: false,
            ScrollChangeSource.User);
    }

    private void EnsureUserZoomOperation(double startingZoomFactor, double targetZoomFactor)
    {
        if (_zoomCorrelationId is not ScrollView.NoCorrelationId)
            return;

        var center = new Point(Viewport.Width * 0.5, Viewport.Height * 0.5);
        _ = BeginZoomOperation(
            startingZoomFactor,
            targetZoomFactor,
            center,
            isAnimated: false,
            ScrollChangeSource.User);
    }

    private void CompleteScrollOperation(ScrollingOperationResult result)
    {
        var correlationId = _scrollCorrelationId;
        _scrollCorrelationId = ScrollView.NoCorrelationId;
        _scrollRequestId = null;
        _scrollRequestAnimated = false;
        _scrollViewOwner?.CompleteScrollOperation(correlationId, result);
    }

    private void CompleteZoomOperation(ScrollingOperationResult result)
    {
        var correlationId = _zoomCorrelationId;
        _zoomCorrelationId = ScrollView.NoCorrelationId;
        _zoomRequestId = null;
        _zoomRequestAnimated = false;
        _scrollViewOwner?.CompleteZoomOperation(correlationId, result);
    }

    private void InterruptOperations()
    {
        CompleteScrollOperation(ScrollingOperationResult.Interrupted);
        CompleteZoomOperation(ScrollingOperationResult.Interrupted);
    }

    private void InterruptOperationsForTrackerRequest()
    {
        if (HasActiveTrackerRequest
            || _interactionState is ScrollingInteractionState.Inertia or ScrollingInteractionState.Animation)
            InterruptOperations();
    }

    private void SetInteractionState(ScrollingInteractionState state)
    {
        _interactionState = state;
        _scrollViewOwner?.UpdateInteractionState(state);
    }


    private void ApplyScrollableArea((Size Extent, Size ScaledExtent, Vector MinPosition, Vector MaxPosition) scrollableArea)
    {
        Extent = scrollableArea.ScaledExtent;

        if (_interactionTracker is null || _interactionSource is null)
        {
            return;
        }

        // Send only layout geometry. The composition thread derives bounds from its own scale
        // and constrains the position atomically, without a delayed UI-thread correction.
        _interactionTracker.ConfigureContentBounds(new InteractionTrackerContentBounds(
            scrollableArea.Extent,
            Viewport,
            new Vector(GetHorizontalContentAlignmentRatio(), GetVerticalContentAlignmentRatio())));

        var sourceMode = ScrollViewer.GetIsScrollInertiaEnabled(this) ? InteractionSourceMode.EnabledWithInertia : InteractionSourceMode.EnabledWithoutInertia;

        _interactionSource.PositionXSourceMode = CanHorizontallyScroll ? sourceMode : InteractionSourceMode.Disabled;

        _interactionSource.PositionYSourceMode = CanVerticallyScroll || _verticalScrollAttachment?.IsAttached == true
            ? sourceMode : InteractionSourceMode.Disabled;
    }

    private void UpdateComputedScrollMode((Size Extent, Size ScaledExtent, Vector MinPosition, Vector MaxPosition) scrollableArea)
    {
        UpdateCanScroll(
            scrollableArea.MaxPosition.X > scrollableArea.MinPosition.X,
            scrollableArea.MaxPosition.Y > scrollableArea.MinPosition.Y);
    }

    private (Size Extent, Size ScaledExtent, Vector MinPosition, Vector MaxPosition) CalculateScrollableArea(double scale)
    {
        if (Child == null)
        {
            return (default, default, default, default);
        }

        var baseExtent = GetContentExtent();
        var scaledExtent = new Size(baseExtent.Width * scale, baseExtent.Height * scale);

        var horizontalAlignment = GetHorizontalContentAlignmentRatio();
        var verticalAlignment = GetVerticalContentAlignmentRatio();
        var horizontalRange = ScrollGeometry.CalculateAxisRange(
            scaledExtent.Width,
            Viewport.Width,
            horizontalAlignment);
        var verticalRange = ScrollGeometry.CalculateAxisRange(
            scaledExtent.Height,
            Viewport.Height,
            verticalAlignment);
        var minPosition = new Vector(horizontalRange.Minimum, verticalRange.Minimum);
        var maxPosition = new Vector(horizontalRange.Maximum, verticalRange.Maximum);

        return (baseExtent, scaledExtent, minPosition, maxPosition);
    }

    /// <summary>
    /// Gets the unscaled content extent used to calculate the scrollable area.
    /// </summary>
    protected virtual Size GetContentExtent()
    {
        if (Child is not { } child)
            return default;

        var childMargin = child.Margin + Padding;
        if (child.UseLayoutRounding)
        {
            var layoutScale = LayoutHelper.GetLayoutScale(child);
            childMargin = LayoutHelper.RoundLayoutThickness(childMargin, layoutScale);
        }

        return child.Bounds.Size.Inflate(childMargin);
    }

    /// <summary>
    /// Gets the horizontal content alignment ratio used by scroll geometry calculations.
    /// </summary>
    protected virtual double GetHorizontalContentAlignmentRatio() => Child?.HorizontalAlignment switch
    {
        HorizontalAlignment.Center or HorizontalAlignment.Stretch => 0.5,
        HorizontalAlignment.Right => 1,
        _ => 0
    };

    /// <summary>
    /// Gets the vertical content alignment ratio used by scroll geometry calculations.
    /// </summary>
    protected virtual double GetVerticalContentAlignmentRatio() => Child?.VerticalAlignment switch
    {
        VerticalAlignment.Center or VerticalAlignment.Stretch => 0.5,
        VerticalAlignment.Bottom => 1,
        _ => 0
    };

    private static Vector ToTrackerPosition(
        Vector offset,
        (Size Extent, Size ScaledExtent, Vector MinPosition, Vector MaxPosition) scrollableArea) =>
        ScrollGeometry.ToTrackerPosition(offset, scrollableArea.MinPosition, scrollableArea.MaxPosition);

    /// <summary>
    /// Gets the content translation used by the current arrange pass.
    /// </summary>
    protected virtual Vector GetArrangeOffset() => -_trackerPosition;

    private static Vector FromTrackerPosition(
        Vector position,
        (Size Extent, Size ScaledExtent, Vector MinPosition, Vector MaxPosition) scrollableArea) =>
        ScrollGeometry.FromTrackerPosition(position, scrollableArea.MinPosition, scrollableArea.MaxPosition);

    private void SynchronizeScrollViewOwner(ScrollChangeSource source)
    {
        if (_scrollViewOwner is null)
            return;

        var logicalExtent = CalculateScrollableArea(ZoomFactor).Extent;
        _scrollViewOwner.UpdateFromPresenter(Extent, logicalExtent, Viewport, Offset, ZoomFactor, source);
    }

    /// <summary>
    /// Make sure that the scroll/scale animation is created and started.
    /// </summary>
    private void EnsureScrollAnimation()
    {
        if (IsLogicalScrollActive || Child is null || _interactionTracker is null || !Child.IsAttachedToVisualTree())
            return;
        var compositionVisual = ElementComposition.GetElementVisual(Child)!;
        SynchronizeCompositionVisual(compositionVisual);
        _animationGroup ??= CreateScrollAnimationGroup(compositionVisual);

        compositionVisual.StartAnimationGroup(_animationGroup);

        // Avalonia expression animations are invalidation-driven; starting one does
        // not enqueue an initial evaluation. Force the freshly attached expressions
        // through the same server-side animation pipeline so first render matches
        // the tracker state instead of waiting for the next input delta.
        var serverVisual = compositionVisual.Server;
        compositionVisual.Compositor.PostServerJob(() =>
        {
            serverVisual.Animations?.EvaluateAnimations();
            serverVisual.RecomputeOwnProperties();
        });
    }

    private void ClearScrollAnimation(CompositionVisual? compositionVisual)
    {
        if (_animationGroup is null)
        {
            return;
        }

        compositionVisual?.StopAnimationGroup(_animationGroup);
        _animationGroup.Dispose();
        _animationGroup = null;
    }

    private CompositionAnimationGroup CreateScrollAnimationGroup(CompositionVisual compositionVisual)
    {
        var scrollAnimation = compositionVisual.Compositor.CreateExpressionAnimation();
        scrollAnimation.Expression =
            "Vector3(Margin.X, Margin.Y, 0) - Vector3(Tracker.Position.X, Tracker.Position.Y, Tracker.Position.Z) + Vector3(this.Target.Offset.X, this.Target.Offset.Y, this.Target.Offset.Z)";
        scrollAnimation.Target = "Translation";
        scrollAnimation.SetReferenceParameter("Tracker", _interactionTracker!);

        var margin = Child!.Margin + Padding;
        scrollAnimation.SetVector2Parameter("Margin", new Vector2((float)margin.Left, (float)margin.Top));

        var scaleAnimation = compositionVisual.Compositor.CreateExpressionAnimation();
        scaleAnimation.Expression = "Vector3(Tracker.Scale, Tracker.Scale, Tracker.Scale)";
        scaleAnimation.SetReferenceParameter("Tracker", _interactionTracker!);
        scaleAnimation.Target = "Scale";

        var animationGroup = compositionVisual.Compositor.CreateAnimationGroup();
        animationGroup.Add(scrollAnimation);
        animationGroup.Add(scaleAnimation);
        return animationGroup;
    }

    private void SynchronizeCompositionVisualBeforeFirstAnimation()
    {
        if (Child is null)
            return;

        var compositionVisual = ElementComposition.GetElementVisual(Child);
        if (compositionVisual is null || _animationGroup is not null)
            return;

        SynchronizeCompositionVisual(compositionVisual);
    }

    private void SynchronizeCompositionVisual(CompositionVisual compositionVisual)
    {
        if (Child is null)
            return;

        var fallbackPosition = ToTrackerPosition(Offset, CalculateScrollableArea(ZoomFactor));
        var position = _interactionTracker?.Position ?? new Vector3D(fallbackPosition.X, fallbackPosition.Y, 0);
        var scale = _interactionTracker?.Scale ?? ZoomFactor;

        var visualScale = new Vector3D(scale, scale, scale);
        var margin = Child!.Margin + Padding;
        var offset = compositionVisual.Offset;
        var translation = new Vector3D(
            margin.Left - position.X + offset.X,
            margin.Top - position.Y + offset.Y,
            offset.Z - position.Z);

        compositionVisual.Scale = visualScale;
        compositionVisual.Translation = translation;
    }

    private void UpdateInteractionOptions()
    {
        if (_interactionTracker == null || _interactionSource == null)
            return;

        var sourceMode = ScrollViewer.GetIsScrollInertiaEnabled(this) ? InteractionSourceMode.EnabledWithInertia : InteractionSourceMode.EnabledWithoutInertia;

        _interactionSource.ScaleSourceMode = IsZoomEnabled ? sourceMode : InteractionSourceMode.Disabled;
        _interactionSource.PositionXSourceMode = CanHorizontallyScroll ? sourceMode : InteractionSourceMode.Disabled;
        _interactionSource.PositionYSourceMode = CanVerticallyScroll || _verticalScrollAttachment?.IsAttached == true
            ? sourceMode : InteractionSourceMode.Disabled;
        _interactionSource.GestureBindings = GestureBindings;
        _interactionSource.ScrollInputMultiplier = ScrollInputMultiplier;
        _interactionSource.ZoomInputMultiplier = ZoomInputMultiplier;

        _interactionTracker.PositionInertiaDecayRate = new Vector3D(
            ScrollInertiaDecayRate,
            ScrollInertiaDecayRate,
            ScrollInertiaDecayRate);
        _interactionTracker.ScaleInertiaDecayRate = ZoomInertiaDecayRate;
        _interactionTracker.ConfigurePhysics(OverscrollElasticity, OverscrollBounceRate);

        var chainingMode = IsScrollChainingEnabled ? InteractionChainingMode.Auto : InteractionChainingMode.Never;
        _interactionSource.PositionXChainingMode = chainingMode;
        _interactionSource.PositionYChainingMode = chainingMode;
    }

    /// <summary>
    /// Scrolls to a logical content offset.
    /// </summary>
    /// <param name="offset">The requested logical offset.</param>
    /// <param name="isAnimated"><see langword="true"/> to animate the transition.</param>
    /// <returns>
    /// The attached <see cref="ScrollView"/> operation identifier, or <see cref="ScrollView.NoCorrelationId"/>
    /// when no ScrollView operation is created.
    /// </returns>
    /// <remarks>
    /// Public calls are reported to an attached <see cref="ScrollView"/> as
    /// <see cref="ScrollChangeSource.Programmatic"/> changes.
    /// </remarks>
    public int ScrollTo(Vector offset, bool isAnimated = false) =>
        ScrollTo(offset, isAnimated, ScrollChangeSource.Programmatic);

    internal int ScrollTo(
        Vector offset,
        bool isAnimated,
        ScrollChangeSource source,
        Vector? startingOffset = null)
    {
        offset = ClampOffsetToEnabledAxes(offset);
        if (Offset.NearlyEquals(offset) || _interactionState is ScrollingInteractionState.Interaction)
            return ScrollView.NoCorrelationId;

        var completeOnIdle = HasActiveTrackerRequest
                             || _interactionState is ScrollingInteractionState.Inertia or ScrollingInteractionState.Animation;
        InterruptOperationsForTrackerRequest();
        var correlationId = BeginScrollOperation(
            startingOffset ?? Offset,
            offset,
            isAnimated,
            source,
            completeOnIdle);
        if (_scrollViewOwner is { } owner && !owner.IsScrollOperationActive(correlationId))
            return correlationId;

        _changeSource = source;

        if (_interactionTracker is null || GetCompositionVisual() is not { } visual)
        {
            SetCurrentValue(OffsetProperty, offset);
            SynchronizeScrollViewOwner(source);
            Dispatcher.UIThread.Post(
                () => CompleteScrollOperation(ScrollingOperationResult.Completed),
                DispatcherPriority.Render);
            return correlationId;
        }

        var trackerPosition = ToTrackerPosition(offset, CalculateScrollableArea(ZoomFactor));
        _trackerPosition = trackerPosition;
        var position = new Vector3D(trackerPosition.X, trackerPosition.Y, 0);

        if (isAnimated)
        {
            var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(300);
            animation.InsertKeyFrame(1, position, new CircularEaseOut());
            _scrollRequestId = _interactionTracker.TryUpdatePositionWithAnimation(animation);
        }
        else
        {
            _scrollRequestId = _interactionTracker.TryUpdatePosition(position);
            InvalidateArrange();
        }

        _requestId = _scrollRequestId;
        return correlationId;
    }

    private Vector ClampOffsetToEnabledAxes(Vector offset)
    {
        var scrollableArea = CalculateScrollableArea(ZoomFactor);
        var canScrollHorizontally = CanScrollHorizontally(
            scrollableArea.MaxPosition.X > scrollableArea.MinPosition.X);
        var canScrollVertically = CanScrollVertically(
            scrollableArea.MaxPosition.Y > scrollableArea.MinPosition.Y);
        return new(
            canScrollHorizontally ? offset.X : 0,
            canScrollVertically ? offset.Y : 0);
    }

    /// <summary>
    /// Resolves whether the horizontal axis accepts scrolling requests.
    /// </summary>
    protected virtual bool CanScrollHorizontally(bool hasScrollableRange) =>
        ScrollView.CanScroll(HorizontalScrollMode, hasScrollableRange);

    /// <summary>
    /// Resolves whether the vertical axis accepts scrolling requests.
    /// </summary>
    protected virtual bool CanScrollVertically(bool hasScrollableRange) =>
        ScrollView.CanScroll(VerticalScrollMode, hasScrollableRange);

    /// <summary>
    /// Changes the zoom factor by an additive amount.
    /// </summary>
    /// <param name="zoomFactorDelta">The amount added to the current zoom factor.</param>
    /// <param name="isAnimated"><see langword="true"/> to animate the transition.</param>
    /// <returns>The attached <see cref="ScrollView"/> operation identifier, or <see cref="ScrollView.NoCorrelationId"/>.</returns>
    public int ZoomBy(double zoomFactorDelta, bool isAnimated = true) =>
        ZoomBy(zoomFactorDelta, centerPoint: null, isAnimated: isAnimated);

    /// <summary>
    /// Changes the zoom factor by an additive amount around a viewport-relative point.
    /// </summary>
    /// <param name="zoomFactorDelta">The amount added to the current zoom factor.</param>
    /// <param name="centerPoint">
    /// The viewport-relative point that remains visually stationary, or <see langword="null"/> to use the viewport center.
    /// </param>
    /// <param name="isAnimated"><see langword="true"/> to animate the transition.</param>
    /// <returns>The attached <see cref="ScrollView"/> operation identifier, or <see cref="ScrollView.NoCorrelationId"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="centerPoint"/> contains a non-finite coordinate.
    /// </exception>
    public int ZoomBy(double zoomFactorDelta, Point? centerPoint, bool isAnimated = true)
    {
        ValidateZoomCenter(centerPoint);
        var currentScale = _interactionTracker?.Scale ?? ZoomFactor;
        return ZoomTo(
            currentScale + zoomFactorDelta,
            centerPoint,
            isAnimated);
    }

    /// <summary>
    /// Changes the zoom factor to an absolute value.
    /// </summary>
    /// <param name="zoomFactor">The requested zoom factor.</param>
    /// <param name="isAnimated"><see langword="true"/> to animate the transition.</param>
    /// <returns>The attached <see cref="ScrollView"/> operation identifier, or <see cref="ScrollView.NoCorrelationId"/>.</returns>
    /// <remarks>
    /// Public calls are reported to an attached <see cref="ScrollView"/> as
    /// <see cref="ScrollChangeSource.Programmatic"/> changes.
    /// </remarks>
    public int ZoomTo(double zoomFactor, bool isAnimated = true) =>
        ZoomTo(
            zoomFactor,
            centerPoint: null,
            isAnimated: isAnimated,
            source: ScrollChangeSource.Programmatic);

    /// <summary>
    /// Changes the zoom factor to an absolute value around a viewport-relative point.
    /// </summary>
    /// <param name="zoomFactor">The requested zoom factor.</param>
    /// <param name="centerPoint">
    /// The viewport-relative point that remains visually stationary, or <see langword="null"/> to use the viewport center.
    /// </param>
    /// <param name="isAnimated"><see langword="true"/> to animate the transition.</param>
    /// <returns>The attached <see cref="ScrollView"/> operation identifier, or <see cref="ScrollView.NoCorrelationId"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="centerPoint"/> contains a non-finite coordinate.
    /// </exception>
    public int ZoomTo(double zoomFactor, Point? centerPoint, bool isAnimated = true) =>
        ZoomTo(
            zoomFactor,
            centerPoint,
            isAnimated,
            source: ScrollChangeSource.Programmatic);

    internal int ZoomTo(
        double zoomFactor,
        Point? centerPoint,
        bool isAnimated,
        ScrollChangeSource source)
    {
        ValidateZoomCenter(centerPoint);
        var minimumScale = _interactionTracker?.MinScale ?? MinZoomFactor;
        var maximumScale = _interactionTracker?.MaxScale ?? MaxZoomFactor;
        var newScale = Math.Clamp(zoomFactor, minimumScale, maximumScale);
        var currentScale = _interactionTracker?.Scale ?? ZoomFactor;
        if (MathUtilities.AreClose(currentScale, newScale)
            || _interactionState is ScrollingInteractionState.Interaction)
            return ScrollView.NoCorrelationId;

        var resolvedCenterPoint = centerPoint ?? new Point(Viewport.Width * 0.5, Viewport.Height * 0.5);
        var completeOnIdle = HasActiveTrackerRequest
                             || _interactionState is ScrollingInteractionState.Inertia or ScrollingInteractionState.Animation;
        InterruptOperationsForTrackerRequest();
        var correlationId = BeginZoomOperation(
            currentScale,
            newScale,
            resolvedCenterPoint,
            isAnimated,
            source,
            completeOnIdle);
        if (_scrollViewOwner is { } owner && !owner.IsZoomOperationActive(correlationId))
            return correlationId;

        var visual = GetCompositionVisual();
        if (_interactionTracker is null || visual is null)
        {
            try
            {
                _compositionUpdate = true;
                SetCurrentValue(ZoomFactorProperty, newScale);
            }
            finally
            {
                _compositionUpdate = false;
            }

            SynchronizeScrollViewOwner(source);
            Dispatcher.UIThread.Post(
                () => CompleteZoomOperation(ScrollingOperationResult.Completed),
                DispatcherPriority.Render);
            return correlationId;
        }

        var zoomCenter = new Vector3D(resolvedCenterPoint.X, resolvedCenterPoint.Y, 0);
        _changeSource = source;

        if (isAnimated)
        {
            var compositor = visual.Compositor;
            var animation = compositor.CreateDoubleKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(300);
            animation.InsertKeyFrame(1.0f, newScale, new CircularEaseOut());
            _zoomRequestId = _interactionTracker.TryUpdateScaleWithAnimation(animation, zoomCenter);
        }
        else
        {
            _zoomRequestId = _interactionTracker.TryUpdateScale(newScale, zoomCenter);
        }

        _requestId = _zoomRequestId;
        return correlationId;
    }

    internal static void ValidateZoomCenter(Point? centerPoint)
    {
        if (centerPoint is { } point && (!double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new ArgumentOutOfRangeException(nameof(centerPoint), centerPoint, "The zoom center must be finite.");
    }

    private CompositionVisual? GetCompositionVisual()
    {
        if (Child is null || !Child.IsAttachedToVisualTree())
            return null;
        return ElementComposition.GetElementVisual(Child);
    }


    //public ScrollPropertiesSource GetScrollPropertiesSource() => _scrollPropertiesSource ?? CreateScrollPropertiesSource();

    //private ScrollPropertiesSource CreateScrollPropertiesSource()
    //{
    //    if (_scrollPropertiesSource == null &&
    //        CompositionVisual != null &&
    //        _interactionTracker != null)
    //    {
    //        _scrollPropertiesSource = ScrollPropertiesSource.Create(this, _interactionTracker);
    //    }


    //    return _scrollPropertiesSource;
    //}


    /// <summary>
    /// Attaches a compositor participant when this presenter is loaded and physically scrolling.
    /// The returned registration owns the attachment across reloads and logical mode changes.
    /// The callback and its returned disposal run on the UI thread. Detach the participant before disposing outputs.
    /// </summary>
    public IDisposable AttachVerticalScroll(Func<InteractionTracker, IDisposable> attach)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(attach);
        if (_verticalScrollAttachment is not null)
            throw new InvalidOperationException("A vertical scroll attachment is already registered.");
        var registration = new VerticalScrollAttachment(this, attach);
        _verticalScrollAttachment = registration;
        UpdateVerticalScrollAttachment();
        return registration;
    }

    private void UpdateVerticalScrollAttachment()
    {
        if (_presenterLoaded && IsPhysicalScrollActive && _interactionTracker is not null)
            _verticalScrollAttachment?.Attach(_interactionTracker);
        else
            _verticalScrollAttachment?.Detach();
        UpdateInteractionOptions();
    }

    private sealed class VerticalScrollAttachment(ScrollPresenter owner,
        Func<InteractionTracker, IDisposable> attach) : IDisposable
    {
        private InteractionTracker? _tracker;
        private IDisposable? _attachment;
        internal bool IsAttached => _attachment is not null;

        internal void Attach(InteractionTracker tracker)
        {
            if (ReferenceEquals(tracker, _tracker))
                return;
            Detach();
            _tracker = tracker;
            _attachment = attach(tracker);
        }

        internal void Detach()
        {
            _attachment?.Dispose();
            _attachment = null;
            _tracker = null;
        }

        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!ReferenceEquals(owner._verticalScrollAttachment, this))
                return;
            Detach();
            owner._verticalScrollAttachment = null;
            owner.UpdateInteractionOptions();
        }
    }
}
