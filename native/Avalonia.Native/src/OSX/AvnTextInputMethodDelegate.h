//
//  AvnTextInputMethodHost.h
//  Avalonia.Native.OSX
//
//  Created by Benedikt Stebner on 24.11.22.
//  Copyright © 2022 Avalonia. All rights reserved.
//

#ifndef AvnTextInputMethodHost_h
#define AvnTextInputMethodHost_h

@protocol AvnTextInputMethodDelegate
@required
-(void) setText:(NSString* _Nonnull) text;
-(void) setCursorRect:(AvnRect) cursorRect;
-(void) setSelection: (int) start : (int) end;

// Abandons any composition that is still in flight, closing the candidate window with it.
-(void) abandonMarkedText;

@end

#endif /* AvnTextInputMethodHost_h */
