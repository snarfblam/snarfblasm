using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Romulus;

namespace snarfblasm
{
    /// <summary>
    /// Acts as a base class for Pass classes.
    /// </summary>
    abstract class Pass
    {
        public Pass(Assembler assembler) {
            this.Assembler = assembler;
            ResetOrigin();
            Assembly = assembler.Assembly;

            AddDefaultPatch();

            Bank = -1;
            MostRecentNamedLabel = new Identifier("!unnamed!", null);

            Assembler.Evaluator.MostRecentNamedLabelGetter = () => this.MostRecentNamedLabel;
        }

        string _CurrentNamespace = null;

        /// <summary>Current bank. Used for debug output.</summary>
        public int Bank { get; private set; }
        /// <summary>Returns true of a .PATCH directive is encountered.</summary>
        public bool HasPatchDirective { get; private set; }
        /// <summary>Most recent non-anonymous, non-local label. Used to identify current scope for local labels.</summary>
        protected Identifier MostRecentNamedLabel { get; private set; }
        /// <summary>Collection of named values.</summary>
        public snarfblasm.assembler.PassValuesCollection Values = new snarfblasm.assembler.PassValuesCollection();
        /// <summary>The 6502 address the next output will be written to.</summary>
        public int CurrentAddress { get; set; }
        /// <summary>Gets/sets the namespace that values will be declared in and searched for. Set to null to use the default namespace.</summary>
        public string CurrentNamespace {
            get { return _CurrentNamespace; }
            set {
                _CurrentNamespace = Values.CurrentNamespace = value;
            }
        }
        /// <summary>If true, nothing is to be written to the output stream. The assembler will otherwise function as normal
        /// (e.g. address will increase as instructions are processed).</summary>
        public bool SurpressOutput { get; protected set; }
        // Todo: get rid of current output offset. It is no longer used and deprecated


        bool hasORGed = false;

        // Todo: what happens when address is FFFF and data is written? (Should currentaddress verify, and maybe add an error?)
        // Todo: option to stop running the processing loop if a certain number of errors occur (probably with a default value, maybe 10)

        public void RunPass() {
            if (Assembly == null) throw new InvalidOperationException("Must specify assembly before calling PerformPass.");

            PrepNextPointers();
            
            BeforePerformPass();
            PerformPass();
            AfterPerformPass();
        }

        protected virtual void AfterPerformPass() { }
        protected virtual void BeforePerformPass() { }


        /// <summary>If true, this pass has an output stream that should be written with assembled data. This
        /// value should not change during the lifetime of a Pass object. See also SurpressOutput.</summary>
        public bool EmitOutput { get; protected set; }
        public bool ErrorOnUndefinedSymbol { get; protected set; }
        public bool AllowOverflowErrors { get; protected set; }

        private void PerformPass() {

            for (iCurrentInstruction = 0; iCurrentInstruction < Assembly.ParsedInstructions.Count; iCurrentInstruction++) {
                ProcessPendingLabelsAndDirectives();
                if (!InExcludedCode)
                    ProcessCurrentInstruction();
            }

            // Process any remaining directives (those that come after the last instruction)
            iCurrentInstruction = int.MaxValue - 1;
            ProcessPendingLabelsAndDirectives();

            // todo: ensure that there are no open enums or if blocks
            // todo: throw error and stop processing if current address exceeds $FFFF

            //if (EmitOutput) {
            //    CalculateLastPatchSegmentSize();
            //}
        }

        protected abstract void ProcessCurrentInstruction() ;

        /// <summary>
        /// Can be called after a pass completes, provided it emits output, that returns an array containing all output data.
        /// </summary>
        /// <returns></returns>
        public byte[] GetOutput() {
            // Get a list of all segments that can have output, in the order they should appear in the ROM
            List<Segment> allSegments = new List<Segment>(this.segments.Values).FindAll(seg => seg.TargetOffset != null);
            allSegments.Sort((segA, segB) => segB.TargetOffset.Value - segA.TargetOffset.Value);
            int totalSize = 0;
            foreach (var seg in allSegments) totalSize += (int)seg.Output.Length;

            var output = new byte[totalSize];
            var iOutput = 0;
            foreach (var seg in allSegments) {
                var segOutput = seg.GetOutput();
                Array.Copy(segOutput, 0, output, iOutput, segOutput.Length);
                iOutput += segOutput.Length;
            }

            return output;
        }


        // Todo: it looks like we have a field that is redundant (hasORGed versus OriginSet)
        public void SetOrigin(int address) {
            if (hasORGed) {
                int paddingAmt = address - CurrentAddress;

                //if (InPatchMode) {
                //    // Todo: Most likely, .ORG should always pad unless there is no current address

                //    // In PATCH mode, .ORG just begins a new patch segment
                //    if (EmitOutput) {
                //        CalculateLastPatchSegmentSize();
                //        var lastPatch = PatchSegments[PatchSegments.Count - 1];


                //        int newPatchOffset = lastPatch.PatchOffset + lastPatch.Length + (CurrentAddress - address);
                //        this.PatchSegments.Add(new PatchSegment(lastPatch.Start + lastPatch.Length, -1, newPatchOffset));
                //    }
                //    SetAddress(address);
                    
                //} else {
                //    // .ORG (except first) pads to the specified address.
                //    if (paddingAmt < 0) {
                //        throw new ArgumentException("Specified address is less than the current address.");
                //    } else {
                //        OutputBytePadding(paddingAmt, (byte)0);
                //    }
                //}

                    // .ORG (except first) pads to the specified address.
                if (paddingAmt < 0) {
                    throw new ArgumentException("Specified address is less than the current address.");
                } else {
                    OutputBytePadding(paddingAmt, (byte)0);
                }
            } else {
                // First .ORG acts like a .BASE
                SetAddress(address);
            }

            hasORGed = true;
        }
        public void SetAddress(int address) {
            CurrentAddress = address;

            // Any following .ORGs should pad
            hasORGed = true;
        }

        #region ENUM handling
        bool isInEnum;
        bool enumCache_SurpressOutput;
        int enumCache_Address;

        public ErrorCode BeginEnum(int address) {
            if (isInEnum) {
                return ErrorCode.Nested_Enum;
            }

            isInEnum = true;
            enumCache_Address = CurrentAddress;
            enumCache_SurpressOutput = SurpressOutput;

            CurrentAddress = address;
            SurpressOutput = true;

            return ErrorCode.None;
        }

        public ErrorCode EndEnum() {
            if (!isInEnum) {
                return ErrorCode.Extraneous_End_Enum;
            }

            isInEnum = false;
            CurrentAddress = enumCache_Address;
            SurpressOutput = enumCache_SurpressOutput;

            return ErrorCode.None;
        }
        #endregion

        #region IF,IFDEF handling
        struct ifInfo
        {
            /// <summary>Set to true when a block's condition has been met. Any subsequent blocks (ELSEIF or ELSE) should be ignored.</summary>
            public bool foundCase;
            /// <summary>True if the current block should be processed.</summary>
            public bool processCurrentBlock;
            /////// <summary>True if the block occurs in excluded code (this state needs to be restored when the block closes)</summary>
            ////public bool isExcluded;
        }


        Stack<ifInfo> ifStack = new Stack<ifInfo>();

        bool inIfStack { get { return ifStack.Count > 0; } }
        /// <summary>
        /// If true, the pass is currently passing over excluded code, such as code in an IF block where the condition is not met.
        /// </summary>
        public bool InExcludedCode { get { return ifStack.Count > 0 && !ifStack.Peek().processCurrentBlock; } }

        public ErrorCode If_EnterExcludedIf() {
            ifInfo blockInfo;
            // Prevent any code of the if statement from running by...
            blockInfo.foundCase = true; // ...setting foundCase - no other blocks will be considered
            blockInfo.processCurrentBlock = false; // ...clearing processCurrentBlock - this block wont be considered
            ////blockInfo.isExcluded = true; // ...set InExcludedCode=true when ENDIF is reached
            ifStack.Push(blockInfo);

            return ErrorCode.None;
        }
        public ErrorCode If_EnterIf(bool conditionMet) {
            ifInfo blockInfo;
            blockInfo.foundCase = conditionMet;
            blockInfo.processCurrentBlock = conditionMet;
            ////blockInfo.isExcluded = false;
            ifStack.Push(blockInfo);

            return ErrorCode.None;
        }

        /// <summary>
        /// Call this method only when the next block, ELSE or ELSEIF, is encountered, AND NO BLOCKS HAVE BEEN EXECUTED YET.    
        /// </summary>
        /// <param name="conditionMet"></param>
        /// <returns></returns>
        public ErrorCode If_EnterElse(bool conditionMet) {
            if(!inIfStack)
                return ErrorCode.Missing_Preceeding_If;

            var currentBlock = ifStack.Pop();
            currentBlock.foundCase |= conditionMet;
            currentBlock.processCurrentBlock = conditionMet;
            ifStack.Push(currentBlock);

            return ErrorCode.None;
        }

        public ErrorCode If_CloseIf() {
            if (!inIfStack)
                return ErrorCode.Missing_Preceeding_If;

            var ifBlock = ifStack.Pop();
            ////InExcludedCode = ifBlock.isExcluded;

            return ErrorCode.None;
        }

        internal void ProcessIfBlockDirective(ConditionalDirective directive, out Error error) {
            error = Error.None;
            bool? ifCondition;

            switch (directive.part) {
                case ConditionalDirective.ConditionalPart.IF:
                    // We must note IFs in excluded code to respect nesting (avoid misinterpreting inner ENDIF)
                    if (InExcludedCode) {
                        If_EnterExcludedIf();
                    } else {
                        ifCondition = If_EvalCondition(directive, out error);
                        if (error.IsError) return;
                        if(ifCondition == null){
                            error = new Error(ErrorCode.If_Missing_Condition, Error.Msg_MissingCondition,directive.SourceLine);
                            return;
                        }

                        If_EnterIf((bool)ifCondition);
                    }
                    break;
                case ConditionalDirective.ConditionalPart.ELSEIF:
                    // We can treat else/else if like it is not in excluded code (if it IS in excluded code, it will look as if 
                    // we already had a block executed, so the else/elseif won't be processed.)
                    if (!inIfStack) {
                        error = new Error(ErrorCode.Missing_Preceeding_If, Error.Msg_NoPreceedingIf, directive.SourceLine);
                        return;
                    }

                    bool considerElseIf = !ifStack.Peek().foundCase; // Has a previous block been executed? (We only process an else if it hasn't)
                    if (considerElseIf) {
                        ifCondition = If_EvalCondition(directive, out error);
                        if (error.IsError) return;
                        if (ifCondition == null) {
                            error = new Error(ErrorCode.If_Missing_Condition, Error.Msg_MissingCondition, directive.SourceLine);
                            return;
                        }

                        If_EnterElse((bool)ifCondition);
                    }
                    break;
                case ConditionalDirective.ConditionalPart.ELSE:
                    // We can treat else/else if like it is not in excluded code (if it IS in excluded code, it will look as if 
                    // we already had a block executed, so the else/elseif won't be processed.)
                    if (!inIfStack) {
                        error = new Error(ErrorCode.Missing_Preceeding_If, Error.Msg_NoPreceedingIf, directive.SourceLine);
                        return;
                    }

                    bool runElse = !ifStack.Peek().foundCase; // Has a previous block been executed? (We only process an else if it hasn't)
                    if (!directive.condition.IsNullOrEmpty ) {
                        error = new Error(ErrorCode.Unexpected_Text, Error.Msg_NoTextExpected, directive.SourceLine);
                        return;
                    }

                    If_EnterElse(runElse);
                    break;
                case ConditionalDirective.ConditionalPart.IFDEF:
                    // We must note IFs in excluded code to respect nesting (avoid misinterpreting inner ENDIF)
                    if (InExcludedCode) {
                        If_EnterExcludedIf();
                    } else {
                        ifCondition = If_SymbolExists(directive);
                        if (ifCondition == null) {
                            error = new Error(ErrorCode.If_Missing_Condition, Error.Msg_MissingCondition, directive.SourceLine);
                            return;
                        }

                        If_EnterIf((bool)ifCondition);
                    }
                    break;
                case ConditionalDirective.ConditionalPart.IFNDEF:
                    // We must note IFs in excluded code to respect nesting (avoid misinterpreting inner ENDIF)
                    if (InExcludedCode) {
                        If_EnterExcludedIf();
                    } else {
                        ifCondition = If_SymbolExists(directive);
                        if (ifCondition == null) {
                            error = new Error(ErrorCode.If_Missing_Condition, Error.Msg_MissingCondition, directive.SourceLine);
                            return;
                        }

                        If_EnterIf(!(bool)ifCondition);
                    }
                    break; 
                case ConditionalDirective.ConditionalPart.ENDIF:
                    If_CloseIf();
                    break;
                default:
                    error = new Error(ErrorCode.Engine_Error, "Unexpected IF block tiye in Pass.ProcessIfBlockDirective");
                    break;
            }
        }

        bool? If_EvalCondition(ConditionalDirective directive, out Error error) {
            error = Error.None;

            if (directive.condition.IsNullOrEmpty) return null;
            var condition = directive.condition;
            var result = Assembler.Evaluator.EvaluateExpression(ref condition,directive.SourceLine,out error);
            return (int)result.Value != 0;
        }
        bool? If_SymbolExists(ConditionalDirective directive) {
            if (directive.condition.IsNullOrEmpty) {
                return null;
            }

            return Values.NameExists(new Identifier(directive.condition.ToString(), null));
        }

        #endregion

        /// <summary>
        /// Sets up pointers for the next label and next directive.
        /// </summary>
        private void PrepNextPointers() {
            if (Assembly.Labels.Count > 0) {
                iNextLabel = 0;
                NextLabel_iInstruction = Assembly.Labels[0].iInstruction;
            } else {
                Set_NoLabels();
            }
            if (Assembly.Directives.Count > 0) {
                iNextDirective = 0;
                NextDirective_iInstruction = Assembly.Directives[0].InstructionIndex;
            } else {
                Set_NoDirectives();
            }
        }

        public AssemblyData Assembly { get; private set; }
        public Assembler Assembler { get; private set; }


        protected void AddError(Error error) {
            Assembler.AddError(error);
        }

        protected void ProcessPendingLabelsAndDirectives() {
            bool doMoreThings = true; // Until we decide that there is nothing pending

            do {
                bool labelPending = NextLabel_iInstruction <= iCurrentInstruction;
                bool directivePending = NextDirective_iInstruction <= iCurrentInstruction;

                // If there is both a label and directive pending, we need to figure which came first
                if (labelPending && directivePending) {
                    bool labelComesFirst = Assembly.Labels[iNextLabel].SourceLine <= Assembly.Directives[iNextDirective].SourceLine;

                    if (labelComesFirst)
                        directivePending = false;
                    else
                        labelPending = false;
                }

                if (labelPending) {
                    if (!InExcludedCode) {
                        ProcessCurrentLabel();
                    }
                    NextLabel();
                } else if (directivePending) {
                    // If we are in excluded code, only conditional directives are processed.
                    if (!InExcludedCode || Assembly.Directives[iNextDirective] is ConditionalDirective)
                        ProcessCurrentDirective();
                    NextDirective();
                } else {
                    doMoreThings = false;
                }
            } while (doMoreThings);
        }

        protected virtual void ProcessCurrentLabel() {
            var label = Assembly.Labels[iNextLabel];
            bool isAnon = label.name[0] == '~';

            if (!label.local && !isAnon) {
                MostRecentNamedLabel = label.GetName();
            }
        }

        private void ProcessCurrentDirective() {
            Error error;
            Assembly.Directives[iNextDirective].Process(this, out error);
            if (error.Code != ErrorCode.None) {
                AddError(error);
            }

            if (Assembly.Directives[iNextDirective] is PatchDirective) {
                HasPatchDirective = true; 
            }
        }

        public int iNextLabel;
        public int NextLabel_iInstruction;
        public int iNextDirective;
        public int NextDirective_iInstruction;

        public int iCurrentInstruction;

        void Set_NoLabels() {
            iNextLabel = -1;
            NextLabel_iInstruction = Int32.MaxValue;
        }
        void Set_NoDirectives() {
            iNextDirective = -1;
            NextDirective_iInstruction = Int32.MaxValue;

        }
        protected void NextLabel() {
            iNextLabel++;
            if (iNextLabel >= Assembly.Labels.Count) {
                Set_NoLabels();
            } else {
                NextLabel_iInstruction = Assembly.Labels[iNextLabel].iInstruction;
            }
        }
        protected void NextDirective() {
            iNextDirective++;
            if (iNextDirective >= Assembly.Directives.Count) {
                Set_NoDirectives();
            } else {
                NextDirective_iInstruction = Assembly.Directives[iNextDirective].InstructionIndex;
            }
        }
        // --------------------------------------------- <--This is a line.

        /// <summary>
        /// Writes a byte to the output stream. If there is no output stream
        /// or SurpressOutput is true, the call is ignored (no errors will occur).
        /// DOES NOT AFFECT CurrentAddress.
        /// </summary>
        /// <param name="b"></param>
        public abstract void WriteByte(byte b); // Todo: why does this not affect CurrentAddress? Address is manually updated elsewhere when this is called. Seems roundabout and error prone.

        /// <summary>
        /// Writes padding to the output stream.  If there is no output stream
        /// or SurpressOutput is true, the call is ignored (no errors will occur).
        /// </summary>
        public abstract void OutputBytePadding(int paddingAmt, byte fillValue);
        /// <summary>
        /// Writes padding to the output stream.  If there is no output stream
        /// or SurpressOutput is true, the call is ignored (no errors will occur).
        /// </summary>
        public abstract void OutputWordPadding(int wordCount, ushort fillValue);

        /// <summary>
        /// Gets the stream that output is written to (if applicable). This stream should not be written to while SurpressOutput is true.
        /// </summary>
        public Stream OutputStream { get; protected set; } // Todo: private set. stream set  by selectin segment


        /// <summary>
        /// Resets the origin address (.ORG). For example, a .PATCH directive resets the origin. Any labels that follow a 
        /// .PATCH without a .ORG in between should cause an error since there is no origin.
        /// </summary>
        public void ResetOrigin() {
            hasORGed = false;
        }

        #region PATCH directive stuffs
        //List<PatchSegment> PatchSegments = new List<PatchSegment>();
        // Todo: make this private
        protected Dictionary<string, Segment> segments = new Dictionary<string, Segment>(StringComparer.OrdinalIgnoreCase);
        // Todo: make this private
        protected Segment currentSegment;


        public bool InPatchMode { get; private set; }

        ///// <summary>
        ///// Specifies the offset that the following code will be patched to. See remarks.
        ///// </summary>
        ///// <remarks>Once a patch offset is applied, any following code will be in "patch mode." .ORG directives will
        ///// not pad. Instead, a .ORG will begin a new patch section.</remarks>
        //public void SetPatchOffset(int offset) {
        //    // Todo: need to be able to create .PATCHes with bank/offset locations
        //    EnablePatchMode();

        //    ResetOrigin();

        //    //if (EmitOutput) {
        //    //    CalculateLastPatchSegmentSize();

        //    //    // Add a new patch segment
        //    //    PatchSegments.Add(new PatchSegment((int)OutputStream.Length, -1, offset));
        //    //}
        //    var newSegment = new Segment(new SegmentTarget(offset));
        //    var newSegmentName = "_anon_seg_" + anonymousSegmentIndex + "_";
        //    anonymousSegmentIndex++;
        //    this.segments.Add(newSegmentName, newSegment);
        //    this.SelectSegment(newSegmentName);
        //}

        int anonymousSegmentIndex = 0;
        /// <summary>
        /// This function is to support the .PATCH segment as a simplified/legacy alternative to .SEGMENT.
        /// Adds the segment and selects it.
        /// </summary>
        public void EnterPatchSegment(Segment patchSeg) {
            // Todo: need to be able to create .PATCHes with bank/offset locations
            EnablePatchMode();


            //if (EmitOutput) {
            //    CalculateLastPatchSegmentSize();

            //    // Add a new patch segment
            //    PatchSegments.Add(new PatchSegment((int)OutputStream.Length, -1, offset));
            //}
            //var newSegment = new Segment(new SegmentTarget(offset));
            var newSegmentName = "_anon_seg_" + anonymousSegmentIndex + "_";
            anonymousSegmentIndex++;
            this.segments.Add(newSegmentName, patchSeg);
            this.SelectSegment(newSegmentName);
        }

        protected void SelectSegment(string segName) {
            if (this.currentSegment != null) {
                this.currentSegment.CurrentAddress = this.CurrentAddress;
            }

            // Todo: meaningful error message. likely via a name verification in seg selection directive.
            this.currentSegment = this.segments[segName];
            this.OutputStream = this.currentSegment.Output;

            var segAddress = this.currentSegment.CurrentAddress ?? this.currentSegment.Base;
            if(segAddress == null) {
                ResetOrigin();
            }else{
                this.CurrentAddress = segAddress.Value;
            }

            OnSegmentSelected(segName, this.currentSegment);
        }

        protected abstract void OnSegmentSelected(string name, Segment segment);

        ///// <summary>
        ///// Computes length of the most recently added patch segment. SEE REMARKS
        ///// </summary>
        ///// <remarks>It is assumed that the last patch extends to the end of the code stream.</remarks>
        ///// <returns></returns>
        //private void CalculateLastPatchSegmentSize() {
        //    // 

        //    if (EmitOutput) {
        //        var lastPatch = PatchSegments[PatchSegments.Count - 1];
        //        int streamSize = (int)OutputStream.Length;
        //        int patchLen = streamSize - lastPatch.Start;
        //        PatchSegments[PatchSegments.Count - 1] = new PatchSegment(lastPatch.Start, patchLen, lastPatch.PatchOffset);
        //    } else {
        //        throw new InvalidOperationException("CalculateLastPatchSegmentSize() not valid on a pass that does not emit output.");
        //    }
        //}

        /// <summary>
        /// Prompts the assembler to generate a patch file instead of a plain binary file
        /// </summary>
        /// <param name="offset"></param>
        protected void EnablePatchMode() {
            // Todo: this will need to be replaced with something segment friendly
            InPatchMode = true;
        }

        // Todo: rename AddDefaultSegment
        private void AddDefaultPatch() {
            //PatchSegments.Add(new PatchSegment(0, -1, -1));
            //SetPatchOffset(0); // Todo: verify this makes sense
            var defaultSeg = new Segment(0);
            EnterPatchSegment(defaultSeg);
        }

        public IList<PatchSegment> GetPatchSegments() {
            //for (int i = PatchSegments.Count - 1; i >= 0; i--) {
            //    if (PatchSegments[i].Length == 0 && PatchSegments.Count > 1) {
            //        PatchSegments.RemoveAt(i);
            //    }
            //}

            //return PatchSegments;

            // Todo: this assumes that GetOutput will return segments in order. While this is true, it's not contractually obligated. Probably not a concern since segments are replacing patches anyways, but imma leave dis note to be safe.

            // Get a list of all segments that have output, in the order they should appear in the ROM
            List<Segment> allSegments = new List<Segment>(this.segments.Values).FindAll(seg => seg.TargetOffset != null && seg.Output.Length > 0);
            allSegments.Sort((segA, segB) => segB.TargetOffset.Value - segA.TargetOffset.Value);

            List<PatchSegment> result = new List<PatchSegment>();
            int iOutput = 0;
            foreach (var seg in allSegments) {
                result.Add(new PatchSegment(iOutput, (int)seg.Output.Length, seg.TargetOffset.Value));
                iOutput += (int)seg.Output.Length;
            }

            return result;
        }
      
        #endregion

    }

    class FirstPass : Pass
    {
        public FirstPass(Assembler asm)
            : base(asm) {
            EmitOutput = false;
            AllowOverflowErrors = false;
            ErrorOnUndefinedSymbol = false;
        }

        protected override void BeforePerformPass() {
            base.BeforePerformPass();

            // Assume undefined symbols are unprocessed labels (if not, they will be caught on second pass)
            Assembler.Evaluator.ErrorOnUndefinedSymbols = false;
        }

        protected override void ProcessCurrentInstruction() {

            var currentLine = Assembly.ParsedInstructions[iCurrentInstruction];
            var opcode = Opcode.allOps[currentLine.opcode];

            bool SingleByteOperand = false;
            if (currentLine.operandExpression != null) {
                // If an AsmValue has not been resolved to a literal value yet, it is because it references a label, and thus must be 16-bit

                Error error;

                StringSection expression = currentLine.operandExpression;
                SingleByteOperand = Assembler.Evaluator.EvaluateExpression(ref expression, currentLine.sourceLine, out error).IsByte;
                if (error.Code != ErrorCode.None) {
                    AddError(new Error(error, currentLine.sourceLine));
                } else if (!expression.IsNullOrEmpty) {
                    AddError(new Error(ErrorCode.Invalid_Expression, Error.Msg_InvalidExpression, currentLine.sourceLine));
                }

            } else if (currentLine.operandValue.IsByte) {
                SingleByteOperand = true;
            }

            if (SingleByteOperand) {
                currentLine = Assembly.TryToConvertToZeroPage(iCurrentInstruction, Assembler.AllowInvalidOpcodes);
                //if (TryToConvertToZeroPage(ref currentLine, Assembler.AllowInvalidOpcodes)) {
                //    // If the instruction can be coded as zero-page, update it
                //    Assembly.ParsedInstructions[iCurrentInstruction] = currentLine; // Todo: consider method such as UpdateParsedInstruction
                //}
            }

            var instructionLen = Opcode.GetParamBytes(currentLine.opcode) + 1;
            CurrentAddress += instructionLen;
        }





        protected override void ProcessCurrentLabel() {
            base.ProcessCurrentLabel();

            var label = Assembly.Labels[iNextLabel];

            label.address = (ushort)CurrentAddress;
            label.nspace = CurrentNamespace;
            Assembly.Labels[iNextLabel] = label;

            bool isAnonymous = label.name[0] == '~';
            if (isAnonymous) {
                Assembly.AnonymousLabels.ResolveNextPointer((ushort)CurrentAddress);
            } else {
                var lblName = label.GetName();
                if (Values.NameExists(lblName)) {
                    AddError(new Error(ErrorCode.Value_Already_Defined, string.Format(Error.Msg_ValueAlreadyDefined_name, label.name), label.SourceLine));
                } else {
                    bool isFixedError; // Todo: handle this  (ummmm...don't know what this even is anymore)
                    if (label.address < 0x100) {
                        Values.SetValue(lblName, new LiteralValue((ushort)CurrentAddress, true), true, out isFixedError);
                    } else {
                        Values.SetValue(lblName, new LiteralValue((ushort)CurrentAddress, false), true, out isFixedError);
                    }
                }
            }
        }

        public override void WriteByte(byte b) {
        }


        protected override void OnSegmentSelected(string name, Segment segment) {
            // do nothing
        }

        //public override byte[] GetOutput() {
        //    throw new NotImplementedException();
        //}

        public override void OutputBytePadding(int paddingAmt, byte fillValue) {
            CurrentAddress += paddingAmt;
        }

        public override void OutputWordPadding(int wordCount, ushort fillValue) {
            CurrentAddress += wordCount * 2;
            
        }



    }

    class SecondPass : Pass
    {
        public const string defaultSegmentName = "#default#";
        public SecondPass(Assembler asm)
            : base(asm) {
            EmitOutput = true;
            AllowOverflowErrors = true;
            ErrorOnUndefinedSymbol = true;

            //base.OutputStream = this.outputStream;
            var defaultSeg =  new Segment(0);
            segments.Add(defaultSegmentName, defaultSeg);
            SelectSegment(defaultSegmentName);
        }

        //Stream _OutputStream = new MemoryStream();

        
        
        //public override byte[] GetOutput() {
        //    return output;
        //}

        protected override void OnSegmentSelected(string name, Segment segment) {
            // Todo: does anything need to happen here?
        }

        protected override void BeforePerformPass() {
            base.BeforePerformPass();

            LoadLabelValues();

            Assembler.Evaluator.ErrorOnUndefinedSymbols = true;
        }
        ////protected override void PerformPass() {

        protected override void AfterPerformPass() {
            base.AfterPerformPass();

            
            // no longer necessary
            //// output = _OutputStream.ToArray();
            ////_OutputStream = null;

        }


        protected override void ProcessCurrentInstruction() {
            Error error;

            var currentLine = Assembly.ParsedInstructions[iCurrentInstruction];

            if(!SurpressOutput)
                OutputStream.WriteByte(currentLine.opcode);

            var addressing = Opcode.allOps[currentLine.opcode].addressing;
            switch (addressing) {
                case Opcode.addressing.implied:
                    // No operand
                    CurrentAddress++;
                    break;
                case Opcode.addressing.absoluteIndexedX:
                case Opcode.addressing.absoluteIndexedY:
                case Opcode.addressing.absolute:
                case Opcode.addressing.indirect:
                    // 16-bit opeand
                    ushort operand16 = GetOperand(currentLine, out error);
                    if (error.Code == ErrorCode.None) {
                        if (!SurpressOutput) {
                            OutputStream.WriteByte((byte)(operand16 & 0xFF));
                            OutputStream.WriteByte((byte)(operand16 >> 8));
                        }

                        CurrentAddress += 3;
                    } else {
                        AddError(error);
                    }
                    break;
                case Opcode.addressing.zeropageIndexedX:
                case Opcode.addressing.zeropageIndexedY:
                case Opcode.addressing.zeropage:
                case Opcode.addressing.indirectX:
                case Opcode.addressing.indirectY:
                case Opcode.addressing.immediate:

                    // 8-bit operand
                    ushort operand8 = GetOperand(currentLine, out error);
                    if (error.Code == ErrorCode.None) {
                        if (operand8 > 0xFF) {
                            if (IsZeroPage(addressing)) {
                                error = new Error(ErrorCode.Address_Out_Of_Range, Error.Msg_ZeroPageOutOfRange, currentLine.sourceLine);
                            } else {
                                error = new Error(ErrorCode.Overflow, Error.Msg_ValueOutOfRange, currentLine.sourceLine);
                            }
                            AddError(error);
                        }
                        if (!SurpressOutput)
                            OutputStream.WriteByte((byte)(operand8 & 0xFF));

                        CurrentAddress += 2;
                    } else {
                        AddError(error);
                    }
                    break;
                case Opcode.addressing.relative:
                    // Calculate relative offset
                    int operandAbs = GetOperand(currentLine, out error);
                    if (error.Code == ErrorCode.None) {
                        int relativeOrigin = CurrentAddress + 2;
                        int operandRel = operandAbs - relativeOrigin;
                        // Verify in range
                        if (operandRel < -128 || operandRel > 127) {
                            error = new Error(ErrorCode.Overflow, Error.Msg_BranchOutOfRange, currentLine.sourceLine);
                            AddError(error);
                        }
                        if (!SurpressOutput)
                            OutputStream.WriteByte((byte)operandRel);

                        CurrentAddress += 2;
                    } else {
                        AddError(error);
                    }
                    break;
            }
        }

        private bool IsZeroPage(Opcode.addressing addressing) {
            switch (addressing) {
                case Opcode.addressing.zeropage:
                case Opcode.addressing.zeropageIndexedX:
                case Opcode.addressing.zeropageIndexedY:
                    return true;
                case Opcode.addressing.implied:
                case Opcode.addressing.immediate:
                case Opcode.addressing.absolute:
                case Opcode.addressing.indirect:
                case Opcode.addressing.indirectX:
                case Opcode.addressing.indirectY:
                case Opcode.addressing.relative:
                case Opcode.addressing.absoluteIndexedX:
                case Opcode.addressing.absoluteIndexedY:
                default:
                    return false;
            }
        }

        private void LoadLabelValues() {
            bool isFixedError; // Todo: handle this
            foreach (var label in Assembly.Labels) {
                Values.SetValue(label.GetName(), new LiteralValue((ushort)label.address, false),true,out isFixedError);
            }
        }

        private ushort GetOperand(ParsedInstruction currentLine, out Error error) {
            if (currentLine.operandExpression == null) {
                error = Error.None;
                return currentLine.operandValue.Value;
            }
            if (currentLine.operandExpression != null) {
                StringSection expression = currentLine.operandExpression;

                var result = Assembler.Evaluator.EvaluateExpression(ref expression, currentLine.sourceLine, out error).Value;

                if (error.Code != ErrorCode.None) {
                    error = new Error(error, currentLine.sourceLine);
                    return 0;
                } else if (!expression.IsNullOrEmpty) {
                    error = new Error(ErrorCode.Invalid_Expression, Error.Msg_InvalidExpression, currentLine.sourceLine);
                    return 0;
                }
                return result;
            }

            error = new Error(ErrorCode.Engine_Error, "Instruction required an operand, but none was present. Instruction may have been mis-parsed.", currentLine.sourceLine);
            return 0;
        }


        protected override void ProcessCurrentLabel() {
            base.ProcessCurrentLabel();

            // Nothing to do with labels for second pass.
            var label = Assembly.Labels[iNextLabel];

            bool isAnonymous = label.name != null && label.name.StartsWith("~");
            if (!isAnonymous) {

                if (label.address < 0x8000) {
                    // RAM
                    Assembler.AddDebugLabel(-1, CurrentAddress, label.name);
                }
                if(Bank >= 0 ){
                    // ROM
                    Assembler.AddDebugLabel(Bank, CurrentAddress, label.name);
                }
            }
        }

        public override void WriteByte(byte b) {
            if (!SurpressOutput)
                OutputStream.WriteByte(b);
        }

        public override void OutputBytePadding(int paddingAmt, byte fillValue) {
            if (!SurpressOutput) {
                for (int i = 0; i < paddingAmt; i++) {
                    OutputStream.WriteByte(fillValue);
                }
            }

            CurrentAddress += paddingAmt;
        }

        public override void OutputWordPadding(int wordCount, ushort fillValue) {
            if (!SurpressOutput) {
                for (int i = 0; i < wordCount; i++) {
                    OutputStream.WriteByte((byte)fillValue);
                    OutputStream.WriteByte((byte)(fillValue >> 8));
                }
            }

            CurrentAddress += wordCount * 2;
        }


    }
}
