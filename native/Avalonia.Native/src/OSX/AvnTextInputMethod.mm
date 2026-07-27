//
//  AvnTextInputMethod.mm
//  Avalonia.Native.OSX
//
//  Created by Benedikt Stebner on 23.11.22.
//  Copyright © 2022 Avalonia. All rights reserved.
//

#include "AvnTextInputMethod.h"

AvnTextInputMethod::~AvnTextInputMethod() {
    Client = nullptr;
}

AvnTextInputMethod::AvnTextInputMethod(id<AvnTextInputMethodDelegate> inputMethodDelegate) {
    _inputMethodDelegate = inputMethodDelegate;
}

bool AvnTextInputMethod::IsActive() {
    return Client != nullptr;
}

HRESULT AvnTextInputMethod::SetClient(IAvnTextInputMethodClient *client) {
    START_COM_CALL;
    
    Client = client;
    
    return S_OK;
}

void AvnTextInputMethod::Reset() {
    // Called when the client detaches, which for a focus change between two controls of the same
    // window is the only signal we get: the NSView stays first responder throughout, so the view's
    // own focus callbacks never run. Without this the composition survives the focus change and
    // the still open candidate window commits into whatever control is focused next.
    [_inputMethodDelegate abandonMarkedText];
}

void AvnTextInputMethod::SetSurroundingText(char* text, int start, int end) {
    // stringWithUTF8String: returns nil for malformed input and must not be passed NULL at all,
    // while the delegate takes a _Nonnull string.
    NSString* surroundingText = text != nullptr ? [NSString stringWithUTF8String:text] : nil;

    [_inputMethodDelegate setText:surroundingText != nil ? surroundingText : @""];
    [_inputMethodDelegate setSelection: start:end];
}

void AvnTextInputMethod::SetCursorRect(AvnRect rect) {
    [_inputMethodDelegate setCursorRect: rect];
}

void AvnTextInputMethod::SetSelectionInSurroundingText(int start, int end) {
    [_inputMethodDelegate setSelection: start:end];
}
