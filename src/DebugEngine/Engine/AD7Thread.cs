using System.Collections.Generic;
using System.Diagnostics;
using DebugEngine.Node;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;

namespace DebugEngine.Engine
{
    // This class implements IDebugThread2 which represents a thread running in a program.
    internal class AD7Thread : IDebugThread2, IDebugThread100
    {
        private readonly AD7Engine _engine;
        private readonly NodeThread _thread;

        public AD7Thread(AD7Engine engine, NodeThread thread)
        {
            _engine = engine;
            _thread = thread;
        }

        public NodeThread Thread
        {
            get { return _thread; }
        }

        private string Name
        {
            get { return _thread.Name; }
        }

        #region IDebugThread2 Members

        // Determines whether the next statement can be set to the given stack frame and code context.
        // We need to try the step to verify it accurately so we allow say ok here.
        internal const int ECannotSetNextStatementOnException = unchecked((int) 0x80040105);

        int IDebugThread2.CanSetNextStatement(IDebugStackFrame2 stackFrame, IDebugCodeContext2 codeContext)
        {
            return VSConstants.S_OK;
        }

        // Retrieves a list of the stack frames for this thread.
        // We currently call into the process and get the frames.  We might want to cache the frame info.
        int IDebugThread2.EnumFrameInfo(enum_FRAMEINFO_FLAGS dwFieldSpec, uint nRadix, out IEnumDebugFrameInfo2 enumObject)
        {
            IList<NodeStackFrame> stackFrames = _thread.Frames;
            if (stackFrames == null)
            {
                enumObject = null;
                return VSConstants.E_FAIL;
            }

            int numStackFrames = stackFrames.Count;

            var frameInfoArray = new FRAMEINFO[numStackFrames];

            for (int i = 0; i < numStackFrames; i++)
            {
                var frame = new AD7StackFrame(_engine, this, stackFrames[i]);
                frame.SetFrameInfo(dwFieldSpec, out frameInfoArray[i]);
            }

            enumObject = new AD7FrameInfoEnum(frameInfoArray);
            return VSConstants.S_OK;
        }

        // Get the name of the thread. 
        int IDebugThread2.GetName(out string threadName)
        {
            threadName = Name;
            return VSConstants.S_OK;
        }

        // Return the program that this thread belongs to.
        int IDebugThread2.GetProgram(out IDebugProgram2 program)
        {
            program = _engine;
            return VSConstants.S_OK;
        }

        // Gets the system thread identifier.
        int IDebugThread2.GetThreadId(out uint threadId)
        {
            threadId = (uint) _thread.Id;
            return VSConstants.S_OK;
        }

        // Gets properties that describe a thread.
        int IDebugThread2.GetThreadProperties(enum_THREADPROPERTY_FIELDS dwFields, THREADPROPERTIES[] propertiesArray)
        {
            var props = new THREADPROPERTIES();

            if ((dwFields & enum_THREADPROPERTY_FIELDS.TPF_ID) != 0)
            {
                props.dwThreadId = (uint) _thread.Id;
                props.dwFields |= enum_THREADPROPERTY_FIELDS.TPF_ID;
            }
            if ((dwFields & enum_THREADPROPERTY_FIELDS.TPF_SUSPENDCOUNT) != 0)
            {
                // sample debug engine doesn't support suspending threads
                props.dwFields |= enum_THREADPROPERTY_FIELDS.TPF_SUSPENDCOUNT;
            }
            if ((dwFields & enum_THREADPROPERTY_FIELDS.TPF_STATE) != 0)
            {
                props.dwThreadState = (uint) enum_THREADSTATE.THREADSTATE_RUNNING;
                props.dwFields |= enum_THREADPROPERTY_FIELDS.TPF_STATE;
            }
            if ((dwFields & enum_THREADPROPERTY_FIELDS.TPF_PRIORITY) != 0)
            {
                props.bstrPriority = "Normal";
                props.dwFields |= enum_THREADPROPERTY_FIELDS.TPF_PRIORITY;
            }
            if ((dwFields & enum_THREADPROPERTY_FIELDS.TPF_NAME) != 0)
            {
                props.bstrName = Name;
                props.dwFields |= enum_THREADPROPERTY_FIELDS.TPF_NAME;
            }
            if ((dwFields & enum_THREADPROPERTY_FIELDS.TPF_LOCATION) != 0)
            {
                props.bstrLocation = GetCurrentLocation();
                props.dwFields |= enum_THREADPROPERTY_FIELDS.TPF_LOCATION;
            }

            propertiesArray[0] = props;
            return VSConstants.S_OK;
        }

        // Resume a thread.
        // This is called when the user chooses "Unfreeze" from the threads window when a thread has previously been frozen.
        int IDebugThread2.Resume(out uint suspendCount)
        {
            // We don't support suspending/resuming threads
            suspendCount = 0;
            return VSConstants.E_NOTIMPL;
        }

        // Sets the next statement to the given stack frame and code context.
        int IDebugThread2.SetNextStatement(IDebugStackFrame2 stackFrame, IDebugCodeContext2 codeContext)
        {
            var frame = (AD7StackFrame) stackFrame;
            var context = (AD7MemoryAddress) codeContext;

            if (frame.StackFrame.SetLineNumber((int) context.LineNumber + 1))
            {
                return VSConstants.S_OK;
            }

            return VSConstants.E_FAIL;
        }

        // suspend a thread.
        // This is called when the user chooses "Freeze" from the threads window
        int IDebugThread2.Suspend(out uint suspendCount)
        {
            // We don't support suspending/resuming threads
            suspendCount = 0;
            return VSConstants.E_NOTIMPL;
        }

        #endregion

        #region IDebugThread100 Members

        int IDebugThread100.SetThreadDisplayName(string name)
        {
            // Not necessary to implement in the debug engine. Instead
            // it is implemented in the SDM.
            return VSConstants.E_NOTIMPL;
        }

        int IDebugThread100.GetThreadDisplayName(out string name)
        {
            // Not necessary to implement in the debug engine. Instead
            // it is implemented in the SDM, which calls GetThreadProperties100()
            name = "";
            return VSConstants.E_NOTIMPL;
        }

        // Returns whether this thread can be used to do function/property evaluation.
        int IDebugThread100.CanDoFuncEval()
        {
            return VSConstants.S_FALSE;
        }

        int IDebugThread100.SetFlags(uint flags)
        {
            // Not necessary to implement in the debug engine. Instead
            // it is implemented in the SDM.
            return VSConstants.E_NOTIMPL;
        }

        int IDebugThread100.GetFlags(out uint flags)
        {
            // Not necessary to implement in the debug engine. Instead
            // it is implemented in the SDM.
            flags = 0;
            return VSConstants.E_NOTIMPL;
        }

        int IDebugThread100.GetThreadProperties100(uint dwFields, THREADPROPERTIES100[] props)
        {
            // Invoke GetThreadProperties to get the VS7/8/9 properties
            var props90 = new THREADPROPERTIES[1];
            var dwFields90 = (enum_THREADPROPERTY_FIELDS) (dwFields & 0x3f);
            int hRes = ((IDebugThread2) this).GetThreadProperties(dwFields90, props90);
            props[0].bstrLocation = props90[0].bstrLocation;
            props[0].bstrName = props90[0].bstrName;
            props[0].bstrPriority = props90[0].bstrPriority;
            props[0].dwFields = (uint) props90[0].dwFields;
            props[0].dwSuspendCount = props90[0].dwSuspendCount;
            props[0].dwThreadId = props90[0].dwThreadId;
            props[0].dwThreadState = props90[0].dwThreadState;

            // Populate the new fields
            if (hRes == VSConstants.S_OK && dwFields != (uint) dwFields90)
            {
                if ((dwFields & (uint) enum_THREADPROPERTY_FIELDS100.TPF100_DISPLAY_NAME) != 0)
                {
                    // Thread display name is being requested
                    props[0].bstrDisplayName = Name;
                    props[0].dwFields |= (uint) enum_THREADPROPERTY_FIELDS100.TPF100_DISPLAY_NAME;

                    // Give this display name a higher priority than the default (0)
                    // so that it will actually be displayed
                    props[0].DisplayNamePriority = 10;
                    props[0].dwFields |= (uint) enum_THREADPROPERTY_FIELDS100.TPF100_DISPLAY_NAME_PRIORITY;
                }

                if ((dwFields & (uint) enum_THREADPROPERTY_FIELDS100.TPF100_CATEGORY) != 0)
                {
                    // Thread category is being requested
                    if (_thread.IsWorkerThread)
                    {
                        props[0].dwThreadCategory = (uint) EnumThreadCategory.ThreadcategoryWorker;
                    }
                    else
                    {
                        props[0].dwThreadCategory = (uint) EnumThreadCategory.ThreadcategoryMain;
                    }

                    props[0].dwFields |= (uint) enum_THREADPROPERTY_FIELDS100.TPF100_CATEGORY;
                }

                if ((dwFields & (uint) enum_THREADPROPERTY_FIELDS100.TPF100_ID) != 0)
                {
                    // Thread category is being requested
                    props[0].dwThreadId = (uint) _thread.Id;
                    props[0].dwFields |= (uint) enum_THREADPROPERTY_FIELDS100.TPF100_ID;
                }

                if ((dwFields & (uint) enum_THREADPROPERTY_FIELDS100.TPF100_AFFINITY) != 0)
                {
                    // Thread cpu affinity is being requested
                    props[0].AffinityMask = 0;
                    props[0].dwFields |= (uint) enum_THREADPROPERTY_FIELDS100.TPF100_AFFINITY;
                }

                if ((dwFields & (uint) enum_THREADPROPERTY_FIELDS100.TPF100_PRIORITY_ID) != 0)
                {
                    // Thread display name is being requested
                    props[0].priorityId = 0;
                    props[0].dwFields |= (uint) enum_THREADPROPERTY_FIELDS100.TPF100_PRIORITY_ID;
                }
            }

            return hRes;
        }

        private enum EnumThreadCategory
        {
            ThreadcategoryWorker = 0,
            ThreadcategoryUi = (ThreadcategoryWorker + 1),
            ThreadcategoryMain = (ThreadcategoryUi + 1),
            ThreadcategoryRpc = (ThreadcategoryMain + 1),
            ThreadcategoryUnknown = (ThreadcategoryRpc + 1)
        }

        #endregion

        #region Uncalled interface methods

        // These methods are not currently called by the Visual Studio debugger, so they don't need to be implemented

        int IDebugThread2.GetLogicalThread(IDebugStackFrame2 stackFrame, out IDebugLogicalThread2 logicalThread)
        {
            Debug.Fail("This function is not called by the debugger");

            logicalThread = null;
            return VSConstants.E_NOTIMPL;
        }

        int IDebugThread2.SetThreadName(string name)
        {
            Debug.Fail("This function is not called by the debugger");

            return VSConstants.E_NOTIMPL;
        }

        #endregion

        private string GetCurrentLocation()
        {
            if (_thread.Frames != null)
            {
                return _thread.Frames[0].FunctionName;
            }

            return "<unknown location, not in node.js code>";
        }
    }
}