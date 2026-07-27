//
// Created by Dan Walmsley on 05/05/2022.
// Copyright (c) 2022 Avalonia. All rights reserved.
//

#import <AppKit/AppKit.h>
#include "AvnView.h"
#include "automation.h"
#import "WindowInterfaces.h"
#import "WindowImpl.h"

@implementation AvnView
{
    ComObjectWeakPtr<TopLevelImpl> _parent;
    NSTrackingArea* _area;
    AvnInputModifiers _modifierState;
    NSEvent* _lastMouseDownEvent;
    AvnPixelSize _lastPixelSize;
    NSObject<IRenderTarget>* _currentRenderTarget;
    AvnPlatformResizeReason _resizeReason;
    NSRect _cursorRect;
    NSMutableAttributedString* _text;
    NSRange _selectedRange;
    NSRange _markedRange;
    NSEvent* _lastKeyDownEvent;
    NSMutableArray* _accessibilityChildren;
}

- (void)onClosed
{
    @synchronized (self)
    {
        _parent = nullptr;
    }
}

- (NSEvent*) lastMouseDownEvent
{
    return _lastMouseDownEvent;
}

- (void) updateRenderTarget
{
    if(_currentRenderTarget) {
        AvnPixelSize size { MAX(_lastPixelSize.Width, 1), MAX(_lastPixelSize.Height, 1) };
        [_currentRenderTarget resize:size withScale:static_cast<float>([[self window] backingScaleFactor])];
        [self setNeedsDisplayInRect:[self frame]];
    }
}


-(void) setRenderTarget:(NSObject<IRenderTarget>*)target
{
    if([self layer])
    {
        [self layer].delegate = nil;
    }
    _currentRenderTarget = target;
    auto layer = [target layer];
    [self setLayer: layer];
    [layer setDelegate: self];
    layer.needsDisplayOnBoundsChange = YES;
    [self updateRenderTarget];
}

-(void)displayLayer: (CALayer*)layer
{
    [self updateLayer];
}

-(AvnView*)  initWithParent: (TopLevelImpl*) parent
{
    self = [super init];
    [self setWantsLayer:YES];
    [self setLayerContentsPlacement: NSViewLayerContentsPlacementTopLeft];

    [self setCanDrawSubviewsIntoLayer: NO];
    [self setLayerContentsRedrawPolicy: NSViewLayerContentsRedrawDuringViewResize];

    _parent = parent;
    _area = nullptr;
    _lastPixelSize.Height = 100;
    _lastPixelSize.Width = 100;
    [self registerForDraggedTypes: @[@"public.data", GetAvnCustomDataType()]];

    _modifierState = AvnInputModifiersNone;
    
    _text = [[NSMutableAttributedString alloc] initWithString:@""];
    _markedRange = NSMakeRange(0, 0);
    _selectedRange = NSMakeRange(0, 0);
    
    return self;
}

- (BOOL)isFlipped
{
    return YES;
}

- (BOOL)wantsUpdateLayer
{
    return YES;
}

- (BOOL)isOpaque
{
    return YES;
}

- (BOOL)acceptsFirstResponder
{
    return true;
}

- (BOOL)acceptsFirstMouse:(NSEvent *)event
{
    return true;
}

- (BOOL)canBecomeKeyView
{
    return true;
}

-(void)setFrameSize:(NSSize)newSize
{
    [super setFrameSize:newSize];

    if(_area != nullptr)
    {
        [self removeTrackingArea:_area];
        _area = nullptr;
    }

    auto parent = _parent.tryGet();
    if (parent == nullptr)
    {
        return;
    }

    NSRect rect = NSZeroRect;
    rect.size = newSize;

    NSTrackingAreaOptions options = NSTrackingActiveAlways | NSTrackingMouseMoved | NSTrackingMouseEnteredAndExited | NSTrackingEnabledDuringMouseDrag;
    _area = [[NSTrackingArea alloc] initWithRect:rect options:options owner:self userInfo:nullptr];
    [self addTrackingArea:_area];

    parent->UpdateCursor();

    auto fsize = [self convertSizeToBacking: [self frame].size];

    if(_lastPixelSize.Width != (int)fsize.width || _lastPixelSize.Height != (int)fsize.height)
    {
        _lastPixelSize.Width = (int)fsize.width;
        _lastPixelSize.Height = (int)fsize.height;
        [self updateRenderTarget];

        auto reason = [self inLiveResize] ? ResizeUser : _resizeReason;

        parent->TopLevelEvents->Resized(FromNSSize(newSize), reason);
    }
}

- (void)updateLayer
{
    AvnInsidePotentialDeadlock deadlock;
    auto parent = _parent.tryGet();
    if (parent == nullptr)
    {
        return;
    }

    parent->TopLevelEvents->RunRenderPriorityJobs();

    parent = _parent.tryGet();
    if (parent == nullptr)
    {
        return;
    }

    parent->TopLevelEvents->Paint();
}

- (void)drawRect:(NSRect)dirtyRect
{
    return;
}

- (AvnPoint) translateLocalPoint:(AvnPoint)pt
{
    pt.Y = [self bounds].size.height - pt.Y;
    return pt;
}

- (void) viewDidChangeBackingProperties
{
    auto fsize = [self convertSizeToBacking: [self frame].size];
    _lastPixelSize.Width = (int)fsize.width;
    _lastPixelSize.Height = (int)fsize.height;
    [self updateRenderTarget];

    auto parent = _parent.tryGet();
    if(parent != nullptr)
    {
        parent->TopLevelEvents->ScalingChanged([[self window] backingScaleFactor]);
    }

    [super viewDidChangeBackingProperties];
}

- (bool) ignoreUserInput:(bool)trigerInputWhenDisabled
{
    auto parent = _parent.tryGet();
    if(parent == nullptr)
    {
        return TRUE;
    }
    
    id<AvnWindowProtocol> parentWindow = nullptr;

    if([[self window] conformsToProtocol:@protocol(AvnWindowProtocol)]){
        parentWindow = (id<AvnWindowProtocol>)[self window];
    }

    if(parentWindow != nullptr && ![parentWindow shouldTryToHandleEvents])
    {
        if(trigerInputWhenDisabled)
        {
            auto windowImpl = _parent.tryGetWithCast<WindowImpl>();
            
            if(windowImpl == nullptr){
                return FALSE;
            }
            
            windowImpl->WindowEvents->GotInputWhenDisabled();
        }

        return TRUE;
    }

    return FALSE;
}

static void ConvertTilt(NSPoint tilt, float* xTilt, float* yTilt)
{
    *xTilt =  tilt.x * 90;
    *yTilt = -tilt.y * 90;
}

- (void)mouseEvent:(NSEvent *)event withType:(AvnRawMouseEventType) type
{
    bool triggerInputWhenDisabled = type != Move && type != LeaveWindow;

    if([self ignoreUserInput: triggerInputWhenDisabled])
    {
        return;
    }

    NSPoint eventLocation = [event locationInWindow];
    
    auto viewLocation = [self convertPoint:NSMakePoint(0, 0) toView:nil];
    
    auto localPoint = NSMakePoint(eventLocation.x - viewLocation.x, viewLocation.y - eventLocation.y);
    
    auto point = ToAvnPoint(localPoint);
    AvnVector delta = { 0, 0};

    if(type == Wheel)
    {
        auto speed = 5;

        if([event hasPreciseScrollingDeltas])
        {
            speed = 50;
        }

        delta.X = [event scrollingDeltaX] / speed;
        delta.Y = [event scrollingDeltaY] / speed;

        if(delta.X == 0 && delta.Y == 0)
        {
            return;
        }
    }
    else if (type == Magnify)
    {
        delta.X = delta.Y = [event magnification];
    }
    else if (type == Rotate)
    {
        delta.X = delta.Y = [event rotation];
    }
    else if (type == Swipe)
    {
        delta.X = [event deltaX];
        delta.Y = [event deltaY];
    }

    float pressure = 0.5f;
    float xTilt = 0.0f;
    float yTilt = 0.0f;
    AvnPointerDeviceType pointerType = AvnPointerDeviceType::Mouse;

    switch (event.type)
    {
        case NSEventTypeLeftMouseDown:
        case NSEventTypeLeftMouseDragged:
        case NSEventTypeRightMouseDown:
        case NSEventTypeRightMouseDragged:
        case NSEventTypeOtherMouseDown:
        case NSEventTypeOtherMouseDragged:
            switch (event.subtype)
            {
                case NSEventSubtypeTabletPoint:
                    pointerType = AvnPointerDeviceType::Pen;
                    pressure = event.pressure;
                    ConvertTilt(event.tilt, &xTilt, &yTilt);
                    break;
                case NSEventSubtypeTabletProximity:
                    pointerType = AvnPointerDeviceType::Pen;
                    pressure = 0.0f;
                    break;
                default:
                    break;
            }
            break;
        case NSEventTypeLeftMouseUp:
        case NSEventTypeRightMouseUp:
        case NSEventTypeOtherMouseUp:
        case NSEventTypeMouseMoved:
            switch (event.subtype)
            {
                case NSEventSubtypeTabletPoint:
                    pointerType = AvnPointerDeviceType::Pen;
                    pressure = 0.0f;
                    ConvertTilt(event.tilt, &xTilt, &yTilt);
                    break;
                case NSEventSubtypeTabletProximity:
                    pointerType = AvnPointerDeviceType::Pen;
                    pressure = 0.0f;
                    break;
                default:
                    break;
            }
            break;
        default:
            break;
    }

    uint64_t timestamp = static_cast<uint64_t>([event timestamp] * 1000);
    auto modifiers = [self getModifiers:[event modifierFlags]];

    if(type != Move ||
            (
                    [self window] != nil &&
                            (
                                    [[self window] firstResponder] == nil
                                            || ![[[self window] firstResponder] isKindOfClass: [NSView class]]
                            )
            )
            )
    {
        auto windowBase = _parent.tryGetWithCast<WindowBaseImpl>();
        
        if(windowBase != nullptr){
            auto parent = windowBase->Parent.tryGet();
            
            if(parent != nullptr){
                auto parentWindow = parent->Window;
                
                if(parentWindow != nullptr)
                    [parentWindow makeFirstResponder:parent->View];
            }
        } else{
            [self becomeFirstResponder];
        }
    }
       
    auto parent = _parent.tryGet();
    if(parent != nullptr)
    {
        parent->TopLevelEvents->RawMouseEvent(type, pointerType, timestamp, modifiers, point, delta, pressure, xTilt, yTilt);
    }

    [super mouseMoved:event];
}

- (BOOL) resignFirstResponder
{
    auto window = [self window];
    if (window != nullptr && window.keyWindow)
    {
        [self onLostFocus];
    }
    
    return YES;
}

- (void)viewWillMoveToWindow:(NSWindow *)newWindow
{
    auto oldWindow = [self window];
    if (oldWindow == newWindow)
    {
        // viewWillMoveToWindow can be called with the same window when the view hierarchy changes
        return;
    }

    if (oldWindow != nullptr)
    {
        [[NSNotificationCenter defaultCenter]
            removeObserver:self
            name:@"NSWindowDidResignKeyNotification"
            object: oldWindow];
    }

    if (newWindow != nullptr)
    {
        [[NSNotificationCenter defaultCenter]
            addObserver:self
            selector:@selector(windowDidResignKey:)
            name:@"NSWindowDidResignKeyNotification"
            object: newWindow];
    }
}

- (void)windowDidResignKey:(NSNotification*)notification
{
    auto window = [self window];
    if (window != nullptr && notification.object == window && [window firstResponder] == self)
    {
        [self onLostFocus];
    }
}

- (void)onLostFocus
{
    // Focus is moving to another client, so any in flight composition has to go with it.
    // Leaving _markedRange set would make the next composition look like a continuation of
    // this one and anchor it at a stale location.
    [self abandonMarkedText];

    auto parent = _parent.tryGet();
    if (parent)
        parent->TopLevelEvents->LostFocus();
}

- (void)mouseMoved:(NSEvent *)event
{
    [self mouseEvent:event withType:Move];
}

- (void)mouseDown:(NSEvent *)event
{
    _lastMouseDownEvent = event;
    [self mouseEvent:event withType:LeftButtonDown];
}

- (void)otherMouseDown:(NSEvent *)event
{
    _lastMouseDownEvent = event;

    switch(event.buttonNumber)
    {
        case 2:
            [self mouseEvent:event withType:MiddleButtonDown];
            break;
        case 3:
            [self mouseEvent:event withType:XButton1Down];
            break;
        case 4:
            [self mouseEvent:event withType:XButton2Down];
            break;

        default:
            break;
    }
}

- (void)rightMouseDown:(NSEvent *)event
{
    _lastMouseDownEvent = event;
    [self mouseEvent:event withType:RightButtonDown];
}

- (void)mouseUp:(NSEvent *)event
{
    [self mouseEvent:event withType:LeftButtonUp];
}

- (void)otherMouseUp:(NSEvent *)event
{
    switch(event.buttonNumber)
    {
        case 2:
            [self mouseEvent:event withType:MiddleButtonUp];
            break;
        case 3:
            [self mouseEvent:event withType:XButton1Up];
            break;
        case 4:
            [self mouseEvent:event withType:XButton2Up];
            break;

        default:
            break;
    }
}

- (void)rightMouseUp:(NSEvent *)event
{
    [self mouseEvent:event withType:RightButtonUp];
}

- (void)mouseDragged:(NSEvent *)event
{
    [self mouseEvent:event withType:Move];
    [super mouseDragged:event];
}

- (void)otherMouseDragged:(NSEvent *)event
{
    [self mouseEvent:event withType:Move];
    [super otherMouseDragged:event];
}

- (void)rightMouseDragged:(NSEvent *)event
{
    [self mouseEvent:event withType:Move];
    [super rightMouseDragged:event];
}

- (void)scrollWheel:(NSEvent *)event
{
    [self mouseEvent:event withType:Wheel];
    [super scrollWheel:event];
}

- (void)magnifyWithEvent:(NSEvent *)event
{
    [self mouseEvent:event withType:Magnify];
    [super magnifyWithEvent:event];
}

- (void)rotateWithEvent:(NSEvent *)event
{
    [self mouseEvent:event withType:Rotate];
    [super rotateWithEvent:event];
}

- (void)swipeWithEvent:(NSEvent *)event
{
    [self mouseEvent:event withType:Swipe];
    [super swipeWithEvent:event];
}

- (void)mouseEntered:(NSEvent *)event
{
    [self mouseEvent:event withType:Move];
    [super mouseEntered:event];
}

- (void)mouseExited:(NSEvent *)event
{
    [self mouseEvent:event withType:LeaveWindow];
    [super mouseExited:event];
}

- (void) keyboardEvent: (NSEvent *) event withType: (AvnRawKeyEventType)type
{
    auto parent = _parent.tryGet();
    if([self ignoreUserInput: false] || parent == nullptr)
    {
        return;
    }

    auto scanCode = [event keyCode];
    auto key = VirtualKeyFromScanCode(scanCode, [event modifierFlags]);
    auto physicalKey = PhysicalKeyFromScanCode(scanCode);
    auto keySymbol = KeySymbolFromScanCode(scanCode, [event modifierFlags]);
    auto keySymbolUtf8 = keySymbol == nullptr ? nullptr : [keySymbol UTF8String];
    
    auto timestamp = static_cast<uint64_t>([event timestamp] * 1000);
    auto modifiers = [self getModifiers:[event modifierFlags]];

    parent->TopLevelEvents->RawKeyEvent(type, timestamp, modifiers, key, physicalKey, keySymbolUtf8);
}

- (void)setModifiers:(NSEventModifierFlags)modifierFlags
{
    _modifierState = [self getModifiers:modifierFlags];
}

- (void)flagsChanged:(NSEvent *)event
{
    auto newModifierState = [self getModifiers:[event modifierFlags]];

    bool isAltCurrentlyPressed = (_modifierState & Alt) == Alt;
    bool isControlCurrentlyPressed = (_modifierState & Control) == Control;
    bool isShiftCurrentlyPressed = (_modifierState & Shift) == Shift;
    bool isCommandCurrentlyPressed = (_modifierState & Windows) == Windows;

    bool isAltPressed = (newModifierState & Alt) == Alt;
    bool isControlPressed = (newModifierState & Control) == Control;
    bool isShiftPressed = (newModifierState & Shift) == Shift;
    bool isCommandPressed = (newModifierState & Windows) == Windows;

    if (isAltPressed && !isAltCurrentlyPressed)
    {
        [self keyboardEvent:event withType:KeyDown];
    }
    else if (isAltCurrentlyPressed && !isAltPressed)
    {
        [self keyboardEvent:event withType:KeyUp];
    }

    if (isControlPressed && !isControlCurrentlyPressed)
    {
        [self keyboardEvent:event withType:KeyDown];
    }
    else if (isControlCurrentlyPressed && !isControlPressed)
    {
        [self keyboardEvent:event withType:KeyUp];
    }

    if (isShiftPressed && !isShiftCurrentlyPressed)
    {
        [self keyboardEvent:event withType:KeyDown];
    }
    else if(isShiftCurrentlyPressed && !isShiftPressed)
    {
        [self keyboardEvent:event withType:KeyUp];
    }

    if(isCommandPressed && !isCommandCurrentlyPressed)
    {
        [self keyboardEvent:event withType:KeyDown];
    }
    else if(isCommandCurrentlyPressed && ! isCommandPressed)
    {
        [self keyboardEvent:event withType:KeyUp];
    }

    _modifierState = newModifierState;

    [[self inputContext] handleEvent:event];
    [super flagsChanged:event];
}

- (bool) handleKeyDown: (NSTimeInterval) timestamp withKey:(AvnKey)key withPhysicalKey:(AvnPhysicalKey)physicalKey withModifiers:(AvnInputModifiers)modifiers withKeySymbol:(NSString*)keySymbol {
    auto parent = _parent.tryGet();
    return parent->TopLevelEvents->RawKeyEvent(KeyDown, timestamp, modifiers, key, physicalKey, [keySymbol UTF8String]);
}

- (void)keyDown:(NSEvent *)event
{
    auto parent = _parent.tryGet();
    if([self ignoreUserInput: false] || parent == nullptr)
    {
        return;
    }
    
    _lastKeyDownEvent = event;
    
    auto timestamp = static_cast<uint64_t>([event timestamp] * 1000);
    
    auto scanCode = [event keyCode];
    auto key = VirtualKeyFromScanCode(scanCode, [event modifierFlags]);
    auto physicalKey = PhysicalKeyFromScanCode(scanCode);
    auto keySymbol = KeySymbolFromScanCode(scanCode, [event modifierFlags]);
    
    auto modifiers = [self getModifiers:[event modifierFlags]];
    
    //InputMethod is active
    if(parent->InputMethod->IsActive()){
        auto hasInputModifier = modifiers != AvnInputModifiersNone;
        
        //Handle keyDown first if an input modifier is present
        if(hasInputModifier){
            if([self handleKeyDown:timestamp withKey:key withPhysicalKey:physicalKey withModifiers:modifiers withKeySymbol:keySymbol]){
                //User code has handled the event
                _lastKeyDownEvent = nullptr;
                
                return;
            }
        }
        
        if([[self inputContext] handleEvent:event] == NO){
            //KeyDown has not been consumed by the input context
                
            //Only raise a keyDown if we don't have a modifier
            if(!hasInputModifier){
                [self handleKeyDown:timestamp withKey:key withPhysicalKey:physicalKey withModifiers:modifiers withKeySymbol:keySymbol];
            }
        }
        
    }
    //InputMethod not active
    else{
        auto keyDownHandled = [self handleKeyDown:timestamp withKey:key withPhysicalKey:physicalKey withModifiers:modifiers withKeySymbol:keySymbol];
            
        //Raise text input event for unhandled key down
        if(!keyDownHandled){
            if(keySymbol != nullptr && key != AvnKeyEnter){
                auto timestamp = static_cast<uint64_t>([event timestamp] * 1000);
                
                parent->TopLevelEvents->RawTextInputEvent(timestamp, [keySymbol UTF8String]);
            }
        }
    }
    
    _lastKeyDownEvent = nullptr;
}

- (void)keyUp:(NSEvent *)event
{
    [self keyboardEvent:event withType:KeyUp];
    [super keyUp:event];
}

- (void) doCommandBySelector:(SEL)selector{
    if(_lastKeyDownEvent != nullptr){
        [self keyboardEvent:_lastKeyDownEvent withType:KeyDown];
    }
}

- (AvnInputModifiers)getModifiers:(NSEventModifierFlags)mod
{
    unsigned int rv = 0;

    if (mod & NSEventModifierFlagControl)
        rv |= Control;
    if (mod & NSEventModifierFlagShift)
        rv |= Shift;
    if (mod & NSEventModifierFlagOption)
        rv |= Alt;
    if (mod & NSEventModifierFlagCommand)
        rv |= Windows;

    NSUInteger pressedButtons = [NSEvent pressedMouseButtons];
        
    if (pressedButtons & (1 << 0))  // Left mouse button
        rv |= LeftMouseButton;
    if (pressedButtons & (1 << 1))  // Right mouse button
        rv |= RightMouseButton;
    if (pressedButtons & (1 << 2))  // Middle mouse button
        rv |= MiddleMouseButton;
    if (pressedButtons & (1 << 3))  // X1 button
        rv |= XButton1MouseButton;
    if (pressedButtons & (1 << 4))  // X2 button
        rv |= XButton2MouseButton;

    return (AvnInputModifiers)rv;
}

- (BOOL)hasMarkedText
{
    return _markedRange.length > 0;
}

- (NSRange)markedRange
{
    // From the docs: the location of the returned range is NSNotFound if there is no marked text.
    if(![self hasMarkedText])
        return NSMakeRange(NSNotFound, 0);

    return _markedRange;
}

- (NSRange)selectedRange
{
    return _selectedRange;
}

- (void)setMarkedText:(id)string selectedRange:(NSRange)selectedRange replacementRange:(NSRange)replacementRange
{
    // From the docs: string is an NSString or an NSAttributedString.
    NSString* markedText = nil;

    if([string isKindOfClass:[NSAttributedString class]])
    {
        markedText = [string string];
    }
    else if([string isKindOfClass:[NSString class]])
    {
        markedText = (NSString*) string;
    }

    if(markedText == nil)
    {
        markedText = @"";
    }

    auto parent = _parent.tryGet();
    bool inputMethodActive = parent != nullptr && parent->InputMethod->IsActive();

    // From the docs: replacementRange is the range to replace. If it is {NSNotFound, 0} the
    // marked text is replaced, and if there is no marked text the current selection is replaced.
    // The marked text lives in a separate preedit buffer that SetPreeditText replaces wholesale,
    // so the only thing that has to be removed here is content that is part of the document.
    NSRange rangeToRemove = NSMakeRange(NSNotFound, 0);

    if (replacementRange.location != NSNotFound)
    {
        rangeToRemove = replacementRange;
    }
    else if (![self hasMarkedText])
    {
        rangeToRemove = _selectedRange;
    }

    if (inputMethodActive && rangeToRemove.location != NSNotFound && rangeToRemove.length > 0)
    {
        parent->InputMethod->Client->SelectInSurroundingText((int)rangeToRemove.location, (int)(rangeToRemove.location + rangeToRemove.length));
        uint64_t timestamp = static_cast<uint64_t>([NSDate timeIntervalSinceReferenceDate] * 1000);
        parent->TopLevelEvents->RawKeyEvent(KeyDown, timestamp, AvnInputModifiersNone, AvnKeyBack, AvnPhysicalKeyNone, "\b");
        parent->TopLevelEvents->RawKeyEvent(KeyUp, timestamp, AvnInputModifiersNone, AvnKeyBack, AvnPhysicalKeyNone, "\b");
    }

    // Keep the composition anchored where it started. _selectedRange is only refreshed
    // asynchronously by the client, so it must not be re-read while composing.
    NSUInteger markedLocation;

    if ([self hasMarkedText])
    {
        markedLocation = _markedRange.location;
    }
    else if (rangeToRemove.location != NSNotFound)
    {
        markedLocation = rangeToRemove.location;
    }
    else
    {
        markedLocation = _selectedRange.location;
    }

    _markedRange = NSMakeRange(markedLocation, [markedText length]);

    if (inputMethodActive)
    {
        parent->InputMethod->Client->SetPreeditText((char*)[markedText UTF8String]);
    }
}

// Drops the composition state without telling the input context about it. Used when the input
// context is the one driving the change and already knows the composition is over.
- (void)clearMarkedText
{
    auto parent = _parent.tryGet();
    if(parent != nullptr && parent->InputMethod->IsActive()){
        parent->InputMethod->Client->SetPreeditText(nullptr);
    }

    _markedRange = NSMakeRange(_selectedRange.location, 0);
}

// Abandons the composition from our side. discardMarkedText belongs here rather than in
// unmarkText: unmarkText is the input context calling into us, and calling back into it from
// there tears down compositions the IME is still driving, such as a multi clause conversion
// that commits one clause through insertText and keeps composing the rest.
- (void)abandonMarkedText
{
    if(![self hasMarkedText]){
        return;
    }

    [self clearMarkedText];

    if([self inputContext]) {
        [[self inputContext] discardMarkedText];
    }
}

- (void)unmarkText
{
    [self clearMarkedText];
}

- (NSArray<NSString *> *)validAttributesForMarkedText
{
    return [NSArray new];
}

- (NSAttributedString *)attributedSubstringForProposedRange:(NSRange)range actualRange:(NSRangePointer)actualRange
{
    // From the docs: an implementation of this method should be prepared for aRange to be out of bounds.
    // In this case, you should return the intersection of the document's range and aRange.
    // If the location of aRange is completely outside of the document's range, return nil.
    auto finalRange = NSIntersectionRange(range, NSMakeRange(0, _text.length));

    if (finalRange.length == 0)
    {
        if (actualRange) {
            *actualRange = NSMakeRange(NSNotFound, 0);
        }

        return nil;
    }

    if (actualRange) {
        *actualRange = finalRange;
    }

    return [_text attributedSubstringFromRange:finalRange];
}

- (void)insertText:(id)string replacementRange:(NSRange)replacementRange
{
    auto parent = _parent.tryGet();
    if(parent == nullptr){
        return;
    }

    // From the docs: string is an NSString or an NSAttributedString.
    NSString* text = nil;

    if([string isKindOfClass:[NSAttributedString class]])
    {
        text = [string string];
    }
    else if([string isKindOfClass:[NSString class]])
    {
        text = (NSString*) string;
    }

    if (replacementRange.location != NSNotFound &&
        ![self hasMarkedText] &&
        parent->InputMethod->IsActive())
    {
        parent->InputMethod->Client->SelectInSurroundingText((int)replacementRange.location, (int)(replacementRange.location + replacementRange.length));
    }

    [self unmarkText];

    const char* utf8Text = [text UTF8String];

    if(utf8Text == nullptr){
        return;
    }

    uint64_t timestamp = static_cast<uint64_t>([NSDate timeIntervalSinceReferenceDate] * 1000);

    parent->TopLevelEvents->RawTextInputEvent(timestamp, utf8Text);
}

- (NSUInteger)characterIndexForPoint:(NSPoint)point
{
    return NSNotFound;
}

- (NSRect)firstRectForCharacterRange:(NSRange)range actualRange:(NSRangePointer)actualRange
{
    auto parent = _parent.tryGet();

    if(parent == nullptr || !parent->InputMethod->IsActive()){
        if (actualRange) {
            *actualRange = NSMakeRange(NSNotFound, 0);
        }

        return NSZeroRect;
    }

    // We only track a single cursor rect, so the rect we return covers the whole
    // requested range instead of just its first line.
    if (actualRange) {
        *actualRange = range;
    }

    return _cursorRect;
}

- (NSDragOperation)triggerAvnDragEvent: (AvnDragEventType) type info: (id <NSDraggingInfo>)info
{
    NSPoint eventLocation = [info draggingLocation];
    auto viewLocation = [self convertPoint:NSMakePoint(0, 0) toView:nil];
    auto localPoint = NSMakePoint(eventLocation.x - viewLocation.x, viewLocation.y - eventLocation.y);
    auto point = ToAvnPoint(localPoint);
    auto modifiers = [self getModifiers:[[NSApp currentEvent] modifierFlags]];
    NSDragOperation nsop = [info draggingSourceOperationMask];

    auto effects = ConvertDragDropEffects(nsop);
    auto parent = _parent.tryGet();
    if (!parent)
      return NSDragOperationNone;
    int reffects = (int)parent->TopLevelEvents
            ->DragEvent(type, point, modifiers, effects,
                    CreateClipboard([info draggingPasteboard]),
                    GetAvnDataObjectHandleFromDraggingInfo(info));

    NSDragOperation ret = static_cast<NSDragOperation>(0);

    // Ensure that the managed part didn't add any new effects
    reffects = (int)effects & reffects;

    // OSX requires exactly one operation
    if((reffects & (int)AvnDragDropEffects::Copy) != 0)
        ret = NSDragOperationCopy;
    else if((reffects & (int)AvnDragDropEffects::Move) != 0)
        ret = NSDragOperationMove;
    else if((reffects & (int)AvnDragDropEffects::Link) != 0)
        ret = NSDragOperationLink;
    if(ret == 0)
        ret = NSDragOperationNone;
    return ret;
}

- (NSDragOperation)draggingEntered:(id <NSDraggingInfo>)sender
{
    return [self triggerAvnDragEvent: AvnDragEventType::Enter info:sender];
}

- (NSDragOperation)draggingUpdated:(id <NSDraggingInfo>)sender
{
    return [self triggerAvnDragEvent: AvnDragEventType::Over info:sender];
}

- (void)draggingExited:(id <NSDraggingInfo>)sender
{
    [self triggerAvnDragEvent: AvnDragEventType::Leave info:sender];
}

- (BOOL)prepareForDragOperation:(id <NSDraggingInfo>)sender
{
    return [self triggerAvnDragEvent: AvnDragEventType::Over info:sender] != NSDragOperationNone;
}

- (BOOL)performDragOperation:(id <NSDraggingInfo>)sender
{
    return [self triggerAvnDragEvent: AvnDragEventType::Drop info:sender] != NSDragOperationNone;
}

- (void)concludeDragOperation:(nullable id <NSDraggingInfo>)sender
{

}

- (AvnPlatformResizeReason)getResizeReason
{
    return _resizeReason;
}

- (void)setResizeReason:(AvnPlatformResizeReason)reason
{
    _resizeReason = reason;
}

- (NSArray *)accessibilityChildren
{
    if (_accessibilityChildren == nil)
        [self recalculateAccessibiltyChildren];
    return _accessibilityChildren;
}

- (id _Nullable) accessibilityHitTest:(NSPoint)point
{
    if (![[self window] isKindOfClass:[AvnWindow class]])
        return self;

    auto window = (AvnWindow*)[self window];
    auto peer = [window automationPeer];

    if (peer == nullptr || !peer->IsRootProvider())
        return nil;

    auto clientPoint = [window convertPointFromScreen:point];
    auto localPoint = [self translateLocalPoint:ToAvnPoint(clientPoint)];
    auto hit = peer->RootProvider_GetPeerFromPoint(localPoint);
    return [AvnAccessibilityElement acquire:hit];
}

- (void)raiseAccessibilityChildrenChanged
{
    auto changed = _accessibilityChildren ? [NSMutableSet setWithArray:_accessibilityChildren] : [NSMutableSet set];

    [self recalculateAccessibiltyChildren];

    if (_accessibilityChildren)
        [changed addObjectsFromArray:_accessibilityChildren];

    NSAccessibilityPostNotificationWithUserInfo(
        self,
        NSAccessibilityLayoutChangedNotification,
        @{ NSAccessibilityUIElementsKey: [changed allObjects]});
}

- (void)recalculateAccessibiltyChildren
{
    _accessibilityChildren = [[NSMutableArray alloc] init];

    if (![[self window] isKindOfClass:[AvnWindow class]])
    {
        return;
    }

    // The accessibility children of the Window are exposed as children
    // of the AvnView.
    auto window = (AvnWindow*)[self window];
    auto peer = [window automationPeer];
    if (peer == nullptr)
    {
        return;
    }
    auto childPeers = peer->GetChildren();
    auto childCount = childPeers != nullptr ? childPeers->GetCount() : 0;

    if (childCount > 0)
    {
        for (int i = 0; i < childCount; ++i)
        {
            IAvnAutomationPeer* child;

            if (childPeers->Get(i, &child) == S_OK)
            {
                id element = [AvnAccessibilityElement acquire:child];
                [_accessibilityChildren addObject:element];
            }
        }
    }
}

- (void) setText:(NSString *)text{
    [[_text mutableString] setString:text != nil ? text : @""];
}

- (void) setSelection:(int)start :(int)end{
    // The client reports the selection as (anchor, caret), so start can be greater than end
    // for a backwards selection. The indices can also lag behind the surrounding text,
    // so clamp them to the text we currently know about.
    int length = (int)_text.length;

    int from = MAX(0, MIN(MIN(start, end), length));
    int to = MAX(0, MIN(MAX(start, end), length));

    _selectedRange = NSMakeRange((NSUInteger)from, (NSUInteger)(to - from));
}

- (void) setCursorRect:(AvnRect)rect{
    NSRect cursorRect = ToNSRect(rect);
    NSRect windowRectOnScreen = [[self window] convertRectToScreen:self.frame];
    
    windowRectOnScreen.size = cursorRect.size;
    windowRectOnScreen.origin = NSMakePoint(windowRectOnScreen.origin.x + cursorRect.origin.x, windowRectOnScreen.origin.y + self.frame.size.height - cursorRect.origin.y - cursorRect.size.height);
    
    _cursorRect = windowRectOnScreen;
    
    if([self inputContext]) {
        [[self inputContext] invalidateCharacterCoordinates];
    }
}

@end
