﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;

namespace DebugEngine.Node {
    class ExceptionRaisedEventArgs : EventArgs {
        private readonly NodeException _exception;
        private readonly NodeThread _thread;
        private readonly bool _isUnhandled;

        public ExceptionRaisedEventArgs(NodeThread thread, NodeException exception, bool isUnhandled) {
            _thread = thread;
            _exception = exception;
            _isUnhandled = isUnhandled;
        }

        public NodeException Exception {
            get {
                return _exception;
            }
        }

        public NodeThread Thread {
            get {
                return _thread;
            }
        }

        public bool IsUnhandled {
            get {
                return _isUnhandled;
            }
        }
    }
}
