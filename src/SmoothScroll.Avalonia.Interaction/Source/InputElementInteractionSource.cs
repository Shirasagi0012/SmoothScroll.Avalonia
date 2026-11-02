using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using SmoothScroll.Avalonia.Interaction.Helpers;
using Vector = Avalonia.Vector;

namespace SmoothScroll.Avalonia.Interaction;

public sealed class InputElementInteractionSource : IDisposable
{
    private const double TouchpadDeltaScale = 48;
    private const double MouseWheelDeltaScale = 128;
    private const double MouseZoomDragScale = 0.01;
    private static readonly double MouseWheelZoomStep = Math.Log(1.2);

    public InteractionSourceMode ScaleSourceMode { get; set; } = InteractionSourceMode.Disabled;

    public InteractionSourceMode PositionXSourceMode { get; set; } = InteractionSourceMode.EnabledWithInertia;

    public InteractionSourceMode PositionYSourceMode { get; set; } = InteractionSourceMode.EnabledWithInertia;

    public InteractionChainingMode PositionXChainingMode { get; set; } = InteractionChainingMode.Auto;

    public InteractionChainingMode PositionYChainingMode { get; set; } = InteractionChainingMode.Auto;

    public ScrollGestureBindings GestureBindings { get; set; } = ScrollGestureBindings.CreateDefault();

    public double ScrollInputMultiplier { get; set; } = 1;

    public double ZoomInputMultiplier { get; set; } = 1;

    private readonly InteractionTracker _tracker;
    private readonly InputElement _inputElement;
    private readonly double _manipulationStartDistance;
    private IPointer? _firstContact;
    private Point _firstPosition;
    private IPointer? _secondContact;
    private Point _secondPosition;
    private double _previousDistance;
    private Point _previousCenter;
    private bool _isInteracting;
    private Point _pressedPosition;
    private VelocityTracker? _velocityTracker;
    private IScrollable? _horizontalChainingTarget;
    private IScrollable? _verticalChainingTarget;
    private ScrollGestureAction _activeDragAction;
    private KeyModifiers _activeModifiers;
    private Vector _lastTrackedVelocity;
    private bool _hasScaleInput;

    public InputElementInteractionSource(InputElement inputElement, InteractionTracker tracker)
    {
        _inputElement = inputElement;
        _tracker = tracker;
        // Button-like descendants handle their own press/release events. Observe those events
        // without preventing a later drag from promoting to a scroll interaction.
        _inputElement.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _inputElement.AddHandler(
            InputElement.PointerMovedEvent,
            OnPointerMoved,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _inputElement.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _inputElement.PointerCaptureLost += OnPointerCaptureLost;
        _inputElement.PointerWheelChanged += OnPointerWheelChanged;

        if (_inputElement is Visual visual)
        {
            visual.AttachedToVisualTree += OnAttachedToVisualTree;
            visual.DetachedFromVisualTree += OnDetachedFromVisualTree;
        }

        var tapSize = AvaloniaLocator.Current?.GetService<IPlatformSettings>()?.GetTapSize(PointerType.Touch);
        _manipulationStartDistance = (tapSize?.Height ?? 10) / 2;
        UpdateChainingTarget();
    }

    private bool IsTranslationEnabled =>
        PositionXSourceMode is not InteractionSourceMode.Disabled
        || PositionYSourceMode is not InteractionSourceMode.Disabled;

    private bool ShouldAutoScrollVertically =>
        PositionYSourceMode is not InteractionSourceMode.Disabled
        && (HasVerticalScrollRange || !HasHorizontalScrollRange
            || _tracker.CanConsumeVerticalInputAtBoundary(1)
            || _tracker.CanConsumeVerticalInputAtBoundary(-1));

    private bool HasHorizontalScrollRange =>
        _tracker.MaxPosition.X - _tracker.MinPosition.X > 0.5;

    private bool HasVerticalScrollRange =>
        _tracker.MaxPosition.Y - _tracker.MinPosition.Y > 0.5;

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        UpdateChainingTarget();
        // AutoScroll can map a vertical wheel to horizontal motion in this viewer.
        // At a boundary, let an ancestor reinterpret the original wheel on either axis.
        var hasWheelChainingTarget = HasHorizontalChainingTarget || HasVerticalChainingTarget;
        var deltaScale = IsPrecisionTouchpadScroll(e) ? TouchpadDeltaScale : MouseWheelDeltaScale;
        var translation = default(Vector);
        var scaleLogDelta = 0.0;

        ApplyWheelComponent(e.Delta.Y, ScrollInputGesture.MouseWheel, horizontalComponent: false);
        ApplyWheelComponent(e.Delta.X, ScrollInputGesture.MouseWheelTilt, horizontalComponent: true);

        if (translation.X is not 0)
        {
            if (PositionXSourceMode is InteractionSourceMode.Disabled
                || IsAtBoundaryForChaining(
                    -translation.X,
                    _tracker.Position.X,
                    _tracker.MinPosition.X,
                    _tracker.MaxPosition.X,
                    PositionXChainingMode,
                    hasWheelChainingTarget))
            {
                translation = translation.WithX(0);
            }
        }

        if (translation.Y is not 0)
        {
            if (PositionYSourceMode is InteractionSourceMode.Disabled
                || IsAtBoundaryForChaining(
                    -translation.Y,
                    _tracker.Position.Y,
                    _tracker.MinPosition.Y,
                    _tracker.MaxPosition.Y,
                    PositionYChainingMode,
                    hasWheelChainingTarget) && !_tracker.CanConsumeVerticalInputAtBoundary(translation.Y))
            {
                translation = translation.WithY(0);
            }
        }

        var handled = false;
        var directTranslation = SelectTranslationForMode(
            translation,
            InteractionSourceMode.EnabledWithoutInertia);
        if (directTranslation != default)
        {
            _tracker.ApplyWheelDelta(directTranslation, useInertia: false);
            handled = true;
        }

        var inertialTranslation = SelectTranslationForMode(
            translation,
            InteractionSourceMode.EnabledWithInertia);
        if (inertialTranslation != default)
        {
            _tracker.ApplyWheelDelta(inertialTranslation, useInertia: true);
            handled = true;
        }

        if (scaleLogDelta is not 0 && ScaleSourceMode is not InteractionSourceMode.Disabled)
        {
            _tracker.AddScaleVelocity(
                e.GetPosition(_inputElement),
                Math.Exp(scaleLogDelta),
                ScaleSourceMode is InteractionSourceMode.EnabledWithInertia);
            handled = true;
        }

        e.Handled = handled;
        return;

        void ApplyWheelComponent(double delta, ScrollInputGesture gesture, bool horizontalComponent)
        {
            if (delta is 0)
                return;

            var action = ResolveAction(gesture, e.KeyModifiers);
            switch (action)
            {
                case ScrollGestureAction.Pan:
                    if (horizontalComponent)
                        translation += new Vector(-delta * deltaScale * ScrollInputMultiplier, 0);
                    else
                        translation += new Vector(0, -delta * deltaScale * ScrollInputMultiplier);
                    break;
                case ScrollGestureAction.AutoScroll:
                    if (ShouldAutoScrollVertically)
                        translation += new Vector(0, -delta * deltaScale * ScrollInputMultiplier);
                    else if (PositionXSourceMode is not InteractionSourceMode.Disabled)
                        translation += new Vector(-delta * deltaScale * ScrollInputMultiplier, 0);
                    break;
                case ScrollGestureAction.HorizontalScroll:
                    translation += new Vector(-delta * deltaScale * ScrollInputMultiplier, 0);
                    break;
                case ScrollGestureAction.VerticalScroll:
                    translation += new Vector(0, -delta * deltaScale * ScrollInputMultiplier);
                    break;
                case ScrollGestureAction.Zoom:
                    scaleLogDelta += delta * MouseWheelZoomStep * ZoomInputMultiplier;
                    break;
            }
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => UpdateChainingTarget();

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _horizontalChainingTarget = null;
        _verticalChainingTarget = null;
    }

    private bool HasHorizontalChainingTarget => _horizontalChainingTarget is not null;

    private bool HasVerticalChainingTarget => _verticalChainingTarget is not null;

    private void UpdateChainingTarget()
    {
        if (_inputElement is not Visual visual)
        {
            _horizontalChainingTarget = null;
            _verticalChainingTarget = null;
            return;
        }

        // Resolve the target after layout as its CanScroll flags are not reliable during attachment.
        _horizontalChainingTarget = null;
        _verticalChainingTarget = null;
        foreach (var target in visual.GetVisualAncestors().OfType<IScrollable>().Skip(1))
        {
            if (_horizontalChainingTarget is null && target.CanHorizontallyScroll)
                _horizontalChainingTarget = target;
            if (_verticalChainingTarget is null && target.CanVerticallyScroll)
                _verticalChainingTarget = target;
            if (_horizontalChainingTarget is not null && _verticalChainingTarget is not null)
                break;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A descendant scroll source has already won the gesture; keep nested scrolling exclusive.
        if (e.Handled && IsClaimedByDescendantScrollable(e.Pointer))
            return;

        UpdateChainingTarget();
        if (!TryGetPressedAction(e, out var action))
            return;

        var position = e.GetPosition(_inputElement);

        if (_firstContact is not null && !_isInteracting && !IsContactCapturedByThisSource(_firstContact))
            ResetContacts();

        if (_firstContact is not null)
        {
            if (_secondContact is not null)
            {
                if (_isInteracting && e.Pointer.Type is PointerType.Touch or PointerType.Pen)
                {
                    e.PreventGestureRecognition();
                    e.Handled = true;
                }

                return;
            }

            if (e.Pointer == _firstContact
                || e.Pointer.Type is not PointerType.Touch and not PointerType.Pen
                || ResolveAction(ScrollInputGesture.TouchPinch, e.KeyModifiers) is not ScrollGestureAction.Zoom
                || ScaleSourceMode is InteractionSourceMode.Disabled)
            {
                return;
            }

            _secondContact = e.Pointer;
            _secondPosition = position;
            _previousDistance = Math.Max(GetDistance(_firstPosition, _secondPosition), 1);
            _previousCenter = GetCenter(_firstPosition, _secondPosition);
            _pressedPosition = _previousCenter;
            _velocityTracker = new VelocityTracker();
            _velocityTracker.AddPosition(TimeSpan.FromMilliseconds(e.Timestamp), default);
            _lastTrackedVelocity = default;
            _activeModifiers = e.KeyModifiers;
            if (TryStartInteraction(e, _previousCenter))
            {
                // Seed the midpoint before either contact moves. Without this baseline the first
                // pinch update treats the moved midpoint as the origin and visibly jumps.
                _tracker.AddScaleVelocity(_previousCenter, 1, useInertia: false);
            }

            CapturePointer(_firstContact);
            CapturePointer(_secondContact);
            e.PreventGestureRecognition();
            e.Handled = true;
            return;
        }

        _firstContact = e.Pointer;
        _activeDragAction = action;
        _activeModifiers = e.KeyModifiers;
        _pressedPosition = position;
        _firstPosition = position;
        _velocityTracker = new VelocityTracker();
        _velocityTracker.AddPosition(TimeSpan.FromMilliseconds(e.Timestamp), default);
        _lastTrackedVelocity = default;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // A descendant scroll source has already won the gesture; keep nested scrolling exclusive.
        if (e.Handled && IsClaimedByDescendantScrollable(e.Pointer))
            return;

        var position = e.GetPosition(_inputElement);

        if (_secondContact is not null)
        {
            if (e.Pointer == _firstContact)
                _firstPosition = position;
            else if (e.Pointer == _secondContact)
                _secondPosition = position;
            else
                return;

            var currentDistance = Math.Max(GetDistance(_firstPosition, _secondPosition), 1);
            var currentCenter = GetCenter(_firstPosition, _secondPosition);

            if (!_isInteracting && !TryStartInteraction(e, currentCenter))
                return;

            // InteractingState applies the pinch-center translation while updating scale. Feeding the
            // same delta as a separate pan would move the content twice and cause visible jumps.
            var centerDelta = currentCenter - _previousCenter;
            TrackVelocity(e.Timestamp, currentCenter - _pressedPosition);

            var handled = centerDelta != default;
            if (_previousDistance > 0
                && ResolveAction(ScrollInputGesture.TouchPinch, _activeModifiers) is ScrollGestureAction.Zoom)
            {
                var scaleRatio = currentDistance / _previousDistance;
                _tracker.AddScaleVelocity(
                    currentCenter,
                    Math.Pow(scaleRatio, ZoomInputMultiplier),
                    ScaleSourceMode is InteractionSourceMode.EnabledWithInertia);
                _hasScaleInput = true;
                handled = true;
            }

            _previousDistance = currentDistance;
            _previousCenter = currentCenter;
            e.PreventGestureRecognition();
            e.Handled = handled;
            return;
        }

        if (_firstContact is null || e.Pointer != _firstContact)
            return;

        if (!_isInteracting)
        {
            if (!ShouldStartManipulation(position - _pressedPosition, e.Pointer.Type)
                || !TryStartInteraction(e, _pressedPosition))
            {
                return;
            }

            _firstPosition = position;
            return;
        }

        var delta = position - _firstPosition;
        if (_activeDragAction is ScrollGestureAction.Zoom)
        {
            if (delta.Y is not 0 && ScaleSourceMode is not InteractionSourceMode.Disabled)
            {
                var scaleRatio = Math.Exp(-delta.Y * MouseZoomDragScale * ZoomInputMultiplier);
                _tracker.AddScaleVelocity(
                    position,
                    scaleRatio,
                    ScaleSourceMode is InteractionSourceMode.EnabledWithInertia);
                _hasScaleInput = true;
            }
        }
        else
        {
            if (!ApplyTranslation(delta, _activeDragAction))
                return;
            TrackVelocity(e.Timestamp, position - _pressedPosition);
        }

        _firstPosition = position;
        e.Handled = true;
    }

    private bool ApplyTranslation(Vector pointerDelta, ScrollGestureAction action)
    {
        var delta = action switch
        {
            ScrollGestureAction.Pan => pointerDelta,
            ScrollGestureAction.AutoScroll when ShouldAutoScrollVertically =>
                pointerDelta.WithX(0),
            ScrollGestureAction.AutoScroll when PositionXSourceMode is not InteractionSourceMode.Disabled =>
                pointerDelta.WithY(0),
            ScrollGestureAction.HorizontalScroll => pointerDelta.WithY(0),
            ScrollGestureAction.VerticalScroll => pointerDelta.WithX(0),
            _ => default
        };

        if (PositionXSourceMode is InteractionSourceMode.Disabled)
            delta = delta.WithX(0);
        if (PositionYSourceMode is InteractionSourceMode.Disabled)
            delta = delta.WithY(0);
        if (delta == default)
            return false;

        if (ShouldChainDuringInteraction(delta))
        {
            TransferPointerToChainingTarget(_firstContact, delta);
            return false;
        }

        _tracker.ApplyManipulationDelta(-delta * ScrollInputMultiplier);
        return true;
    }

    private Vector SelectTranslationForMode(Vector translation, InteractionSourceMode mode) =>
        new(
            PositionXSourceMode is var xMode && xMode == mode ? translation.X : 0,
            PositionYSourceMode is var yMode && yMode == mode ? translation.Y : 0);

    private void TrackVelocity(ulong timestamp, Vector position)
    {
        if (_velocityTracker is null)
            return;

        _velocityTracker.AddPosition(TimeSpan.FromMilliseconds(timestamp), position);
        _lastTrackedVelocity = _velocityTracker.GetFlingVelocity().PixelsPerSecond;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer == _secondContact)
        {
            _secondContact = null;
            _previousDistance = 0;
            if (_isInteracting)
            {
                ResetSingleContactTracking(e.Timestamp, _firstPosition);
                e.Handled = true;
            }

            return;
        }

        if (_firstContact != e.Pointer)
            return;

        if (_secondContact is not null)
        {
            _firstContact = _secondContact;
            _firstPosition = _secondPosition;
            _secondContact = null;
            _previousDistance = 0;
            ResetSingleContactTracking(e.Timestamp, _firstPosition);
            CapturePointer(_firstContact);
            e.Handled = true;
            return;
        }

        if (!_isInteracting)
        {
            ResetContacts();
            return;
        }

        var velocity = _velocityTracker?.GetFlingVelocity().PixelsPerSecond ?? Vector.Zero;
        if (velocity == default)
            velocity = _lastTrackedVelocity;
        velocity = _activeDragAction switch
        {
            ScrollGestureAction.Pan => velocity,
            ScrollGestureAction.AutoScroll when ShouldAutoScrollVertically =>
                velocity.WithX(0),
            ScrollGestureAction.AutoScroll when PositionXSourceMode is not InteractionSourceMode.Disabled =>
                velocity.WithY(0),
            ScrollGestureAction.HorizontalScroll => velocity.WithY(0),
            ScrollGestureAction.VerticalScroll => velocity.WithX(0),
            _ => default
        };

        if (PositionXSourceMode is not InteractionSourceMode.EnabledWithInertia)
            velocity = velocity.WithX(0);
        if (PositionYSourceMode is not InteractionSourceMode.EnabledWithInertia)
            velocity = velocity.WithY(0);

        var includeScaleVelocity = _hasScaleInput
                                   && ScaleSourceMode is InteractionSourceMode.EnabledWithInertia;

        if (velocity != default || includeScaleVelocity)
        {
            _tracker.StartInertia(
                -new Point(velocity.X * ScrollInputMultiplier, velocity.Y * ScrollInputMultiplier),
                includeScaleVelocity);
        }
        else
        {
            _tracker.CompleteUserManipulation();
        }

        // A normal release ends contact tracking before it relinquishes capture. Otherwise
        // PointerCaptureLost would cancel the inertia request just queued above.
        var releasedContact = _firstContact;
        ResetContacts();
        releasedContact?.Capture(null);
        e.Handled = true;
    }

    private void ResetSingleContactTracking(ulong timestamp, Point position)
    {
        _pressedPosition = position;
        _activeDragAction = ResolveAction(ScrollInputGesture.TouchDrag, _activeModifiers);
        _velocityTracker = new VelocityTracker();
        _velocityTracker.AddPosition(TimeSpan.FromMilliseconds(timestamp), default);
        _lastTrackedVelocity = default;
        _previousCenter = default;
    }

    private void ResetContacts()
    {
        _firstContact = null;
        _secondContact = null;
        _velocityTracker = null;
        _pressedPosition = default;
        _firstPosition = default;
        _secondPosition = default;
        _previousDistance = 0;
        _previousCenter = default;
        _activeDragAction = ScrollGestureAction.None;
        _activeModifiers = default;
        _lastTrackedVelocity = default;
        _hasScaleInput = false;
        _isInteracting = false;
    }

    private bool IsPrecisionTouchpadScroll(PointerWheelEventArgs e) =>
        IsPrecisionTouchpadDelta(e.Delta.X) || IsPrecisionTouchpadDelta(e.Delta.Y);

    private static bool IsPrecisionTouchpadDelta(double delta)
    {
        var absoluteValue = Math.Abs(delta);
        return absoluteValue is not 0 && !MathUtilities.AreClose(absoluteValue, (int)absoluteValue);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer != _firstContact && e.Pointer != _secondContact)
            return;

        if (_isInteracting)
        {
            if (_tracker.HasVerticalScrollParticipant)
                _tracker.CancelScroll();
            else
                _tracker.CompleteUserManipulation();
        }

        ResetContacts();
    }

    private static double GetDistance(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Point GetCenter(Point a, Point b) =>
        new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private bool ShouldStartManipulation(Vector delta, PointerType pointerType)
    {
        if (!IsTranslationEnabled)
            return false;

        if (pointerType is not PointerType.Touch and not PointerType.Pen || _secondContact is not null)
            return true;

        UpdateChainingTarget();
        var xDistance = PositionXSourceMode is InteractionSourceMode.Disabled ? 0 : Math.Abs(delta.X);
        var yDistance = PositionYSourceMode is InteractionSourceMode.Disabled ? 0 : Math.Abs(delta.Y);

        if (xDistance > 0
            && IsAtBoundaryForChaining(
                delta.X,
                _tracker.Position.X,
                _tracker.MinPosition.X,
                _tracker.MaxPosition.X,
                PositionXChainingMode,
                HasHorizontalChainingTarget))
        {
            xDistance = 0;
        }

        if (yDistance > 0
            && IsAtBoundaryForChaining(
                delta.Y,
                _tracker.Position.Y,
                _tracker.MinPosition.Y,
                _tracker.MaxPosition.Y,
                PositionYChainingMode,
                HasVerticalChainingTarget)
            && !_tracker.CanConsumeVerticalInputAtBoundary(-delta.Y * ScrollInputMultiplier))
        {
            yDistance = 0;
        }

        return xDistance >= _manipulationStartDistance || yDistance >= _manipulationStartDistance;
    }

    private bool TryStartInteraction(PointerEventArgs e, Point position)
    {
        if (_isInteracting)
            return true;

        var canPinch = _secondContact is not null
                       && ResolveAction(ScrollInputGesture.TouchPinch, _activeModifiers) is ScrollGestureAction.Zoom
                       && ScaleSourceMode is not InteractionSourceMode.Disabled;
        if (!CanExecute(_activeDragAction) && !canPinch)
            return false;

        _tracker.BeginUserManipulation(position, e.Pointer);
        CapturePointer(_firstContact);
        CapturePointer(_secondContact);
        _isInteracting = true;
        e.PreventGestureRecognition();
        e.Handled = true;
        return true;
    }

    private bool TryGetPressedAction(PointerPressedEventArgs e, out ScrollGestureAction action)
    {
        var gesture = e.Pointer.Type switch
        {
            PointerType.Touch or PointerType.Pen => ScrollInputGesture.TouchDrag,
            PointerType.Mouse when e.Properties.IsLeftButtonPressed => ScrollInputGesture.MouseLeftDrag,
            PointerType.Mouse when e.Properties.IsMiddleButtonPressed => ScrollInputGesture.MouseMiddleDrag,
            PointerType.Mouse when e.Properties.IsRightButtonPressed => ScrollInputGesture.MouseRightDrag,
            _ => (ScrollInputGesture?)null
        };

        action = gesture is { } value ? ResolveAction(value, e.KeyModifiers) : ScrollGestureAction.None;
        if (action is not ScrollGestureAction.None && CanExecute(action))
            return true;

        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen
            && ResolveAction(ScrollInputGesture.TouchPinch, e.KeyModifiers) is ScrollGestureAction.Zoom
            && ScaleSourceMode is not InteractionSourceMode.Disabled)
        {
            action = ScrollGestureAction.None;
            return true;
        }

        return false;
    }

    private ScrollGestureAction ResolveAction(ScrollInputGesture gesture, KeyModifiers modifiers)
        => ScrollGestureResolver.Resolve(GestureBindings, gesture, modifiers);

    private bool CanExecute(ScrollGestureAction action) => action switch
    {
        ScrollGestureAction.Pan => IsTranslationEnabled,
        ScrollGestureAction.AutoScroll => IsTranslationEnabled,
        ScrollGestureAction.HorizontalScroll => PositionXSourceMode is not InteractionSourceMode.Disabled,
        ScrollGestureAction.VerticalScroll => PositionYSourceMode is not InteractionSourceMode.Disabled,
        ScrollGestureAction.Zoom => ScaleSourceMode is not InteractionSourceMode.Disabled,
        _ => false
    };

    private static bool IsAtBoundaryForChaining(
        double userDelta,
        double position,
        double min,
        double max,
        InteractionChainingMode chainingMode,
        bool hasChainingTarget)
    {
        if (!CanChain(chainingMode, hasChainingTarget))
            return false;

        const double tolerance = 0.5;
        return userDelta > 0 && position <= min + tolerance
               || userDelta < 0 && position >= max - tolerance;
    }

    private static bool CanChain(InteractionChainingMode chainingMode, bool hasChainingTarget) =>
        hasChainingTarget && chainingMode is not InteractionChainingMode.Never;

    private bool ShouldChainDuringInteraction(Vector fingerDelta)
    {
        UpdateChainingTarget();
        var xEnabled = PositionXSourceMode is not InteractionSourceMode.Disabled;
        var yEnabled = PositionYSourceMode is not InteractionSourceMode.Disabled;

        var xAtBoundary = !xEnabled || fingerDelta.X is 0
                                    || IsAtBoundaryForChaining(
                                        fingerDelta.X,
                                        _tracker.Position.X,
                                        _tracker.MinPosition.X,
                                        _tracker.MaxPosition.X,
                                        PositionXChainingMode,
                                        HasHorizontalChainingTarget);
        var yAtBoundary = !yEnabled || fingerDelta.Y is 0
                                    || IsAtBoundaryForChaining(
                                        fingerDelta.Y,
                                        _tracker.Position.Y,
                                        _tracker.MinPosition.Y,
                                        _tracker.MaxPosition.Y,
                                        PositionYChainingMode,
                                        HasVerticalChainingTarget)
                                    && !_tracker.CanConsumeVerticalInputAtBoundary(-fingerDelta.Y * ScrollInputMultiplier);

        return xAtBoundary && yAtBoundary;
    }

    private void CapturePointer(IPointer? pointer) => pointer?.Capture(_inputElement);

    private void TransferPointerToChainingTarget(IPointer? pointer, Vector fingerDelta)
    {
        if (pointer is null)
            return;

        // Capture directly to the ancestor interaction source. Releasing to null raises
        // PointerCaptureLost through the whole ancestor chain and clears the parent's pending touch.
        var target = SelectChainingTarget(fingerDelta);
        if (target is InputElement inputElement)
        {
            pointer.Capture(inputElement);
            return;
        }

        pointer.Capture(null);
    }

    private IScrollable? SelectChainingTarget(Vector fingerDelta)
    {
        if (fingerDelta.X is 0)
            return _verticalChainingTarget;
        if (fingerDelta.Y is 0)
            return _horizontalChainingTarget;
        if (ReferenceEquals(_horizontalChainingTarget, _verticalChainingTarget))
            return _horizontalChainingTarget;
        if (_horizontalChainingTarget is null)
            return _verticalChainingTarget;
        if (_verticalChainingTarget is null)
            return _horizontalChainingTarget;

        return Math.Abs(fingerDelta.X) >= Math.Abs(fingerDelta.Y) ? _horizontalChainingTarget : _verticalChainingTarget;
    }

    private bool IsContactCapturedByThisSource(IPointer pointer) =>
        pointer.Captured is Visual captured
        && _inputElement is Visual input
        && (ReferenceEquals(captured, input) || input.IsVisualAncestorOf(captured));

    private bool IsClaimedByDescendantScrollable(IPointer pointer) =>
        pointer.Captured is Visual captured
        && captured is IScrollable
        && _inputElement is Visual input
        && !ReferenceEquals(captured, input)
        && input.IsVisualAncestorOf(captured);

    public void Dispose()
    {
        if (_inputElement is Visual visual)
        {
            visual.AttachedToVisualTree -= OnAttachedToVisualTree;
            visual.DetachedFromVisualTree -= OnDetachedFromVisualTree;
        }

        _inputElement.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _inputElement.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _inputElement.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        _inputElement.PointerCaptureLost -= OnPointerCaptureLost;
        _inputElement.PointerWheelChanged -= OnPointerWheelChanged;
        GC.SuppressFinalize(this);
    }
}
