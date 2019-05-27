using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Romulus;

namespace snarfblasm
{
    /// <summary>
    /// Stores data parsed from assembly code.
    /// </summary>
    class AssemblyData : IValueNamespace
    {
        Assembler assembler;
        public AssemblyData(Assembler asm) {
            this.assembler = asm;

            ParsedInstructions = new List<ParsedInstruction>();
            Labels = new List<NamespacedLabel>();
            Directives = new List<Directive>();
        }

        public string CurrentNamespace { get; set; }

        // Todo: consider making read only (modifications can be done via methods)
        public IList<ParsedInstruction> ParsedInstructions { get; private set; }
        public IList<NamespacedLabel> Labels { get; private set; }
        public IList<Directive> Directives { get; private set; }

        // Todo: move to assembly class, make private, add helper methods
        AnonymousLabelCollection anonymousLabels = new AnonymousLabelCollection();
        public AnonymousLabelCollection AnonymousLabels { get { return anonymousLabels; } }
        int anonLabelIndex = 0;
        /// <summary>
        /// Creates a named label with a unique name to correspond to an anonymous label.
        /// </summary>
        /// <param name="iInstruction"></param>
        /// <param name="iSourceLine"></param>
        internal void TagAnonLabel(int iInstruction, int iSourceLine) {
            Labels.Add(new NamespacedLabel("~" + anonLabelIndex.ToString(), null, iInstruction, iSourceLine, false));
            anonLabelIndex++;
        }

        #region IValueNamespace Members

        public int GetForwardLabel(int labelLevel, int iSourceLine) {
            return AnonymousLabels.FindLabel_Forward(labelLevel, iSourceLine);
        }

        public int GetBackwardLabel(int labelLevel, int iSourceLine) {
            return AnonymousLabels.FindLabel_Back(labelLevel, iSourceLine);
        }
        public int GetForwardBrace(int labelLevel, int iSourceLine) {
            return AnonymousLabels.FindBrace_Forward(labelLevel, iSourceLine);
        }

        public int GetBackwardBrace(int labelLevel, int iSourceLine) {
            return AnonymousLabels.FindBrace_Back(labelLevel, iSourceLine);
        }
        public void SetValue(Identifier name, LiteralValue value, bool isFixed, out Error error) {
            error = Error.None;

            if (assembler.CurrentPass == null)
                throw new InvalidOperationException("Can only access variables when assembler is running a pass.");

            bool isDollar = name.Equals(Identifier.CurrentInstruction);  //Romulus.StringSection.Compare(name, "$", true) == 0;
            if (isDollar) {
                assembler.CurrentPass.SetAddress(value.Value);
            }


            //string nameString = name.ToString();
            bool fixedValueError;
            assembler.CurrentPass.Values.SetValue(name, value, isFixed, out fixedValueError);
            if (fixedValueError) {
                // Todo: replace this with assembler value, or return fixedValueError somehow.
                //throw new Exception("Attempted to assign to a fixed value. This exception should be replaced with an appropriate assembler error.");
                error = new Error(ErrorCode.Value_Already_Defined, string.Format(Error.Msg_ValueAlreadyDefined_name, name.ToString()));
            }
        }

        //public LiteralValue GetValue(NamespacedLabelName name) {
        //    return GetValue(name, StringSection.Empty);
        //}
        public LiteralValue GetValue(Identifier name) {
            bool isDollar = name.Equals(Identifier.CurrentInstruction); // Romulus.StringSection.Compare(name, "$", true) == 0;
            if (isDollar) {
                return new LiteralValue((ushort)assembler.CurrentPass.CurrentAddress, false);
            }

            LiteralValue? result;
            if (null == (result = assembler.CurrentPass.Values.TryGetValue(name))) {
                throw new Exception(); // Todo: Must be more specific, and handled!
            }
            return result.Value;
        }
        //public bool TryGetValue(Romulus.StringSection name, out LiteralValue result) {
        //    return TryGetValue(name, StringSection.Empty, out result);
        //}

        public bool TryGetValue(Identifier name, out LiteralValue result) {
            bool isDollar = name.Equals(Identifier.CurrentInstruction); // Romulus.StringSection.Compare(name, "$", true) == 0;
            if (isDollar) {
                result = new LiteralValue((ushort)assembler.CurrentPass.CurrentAddress, false);
                return true;
            }

            var value = assembler.CurrentPass.Values.TryGetValue(name);
            result = value.HasValue ? value.Value : default(LiteralValue);
            return value.HasValue;
        }

        #endregion
    }

    struct NamespacedLabel
    {
        //public NamespacedLabel(string name, int location, int sourceLine, bool local) {
        //    this.name = name;
        //    this.iInstruction = location;
        //    this.SourceLine = sourceLine;
        //    this.address = 0;
        //    this.local = local;
        //    this.nspace = null;
        //}
        public NamespacedLabel(string name, string @namespace, int location, int sourceLine, bool local) {
            this.name = name;
            this.iInstruction = location;
            this.SourceLine = sourceLine;
            this.address = 0;
            this.local = local;
            this.nspace = @namespace;
        }
        public NamespacedLabel(Identifier name, int location, int sourceLine, bool local) {
            this.name = name.name;
            this.iInstruction = location;
            this.SourceLine = sourceLine;
            this.address = 0;
            this.local = local;
            this.nspace = name.nspace;
        }

        public Identifier GetName() {
            return new Identifier(this.name, this.nspace);
        }
        public readonly string name;
        public readonly string nspace;
        public readonly int iInstruction;
        public readonly int SourceLine;
        public ushort address;
        public bool local;

    }

    /// <summary>
    /// Reference to a NamespacedLabel
    /// </summary>
    struct Identifier : IComparable<Identifier>, IEquatable<Identifier>
    {
        // Todo: consider using string sections instead of strings

        public Identifier(string name, string @namespace) {
            this.name = name;
            this.nspace = @namespace;
        }
        public Identifier(NamespacedLabel label) {
            this.name = label.name;
            this.nspace = label.nspace;
        }

        // Todo: convert to constructor (currently done this way to avoid accidentally created identifiers without namespaces while code is being updated)._
        public static Identifier Simple(string name) {
            return new Identifier(name, null);
        }


        public readonly string name;
        public readonly string nspace;

        public bool IsEmpty { get { return String.IsNullOrEmpty(this.name); } }
        /// <summary>Returns whether this name is 'simple', having no specified namespace. Returns true for empty values.</summary>
        public bool IsSimple { get { return string.IsNullOrEmpty(this.nspace); } }
        public static readonly Identifier Empty;
        public static readonly Identifier CurrentInstruction = new Identifier("$", null);

        public bool Equals(Identifier b) {
            return ((this.nspace ?? string.Empty) == (b.nspace ?? string.Empty))
                && ((this.name ?? string.Empty) == (b.name ?? string.Empty));
        }

        public static bool operator ==(Identifier a, Identifier b) {
            return a.Equals(b);
        }
        public static bool operator !=(Identifier a, Identifier b) {
            return !a.Equals(b);
        }

        public override string ToString() {
            if (string.IsNullOrEmpty(this.nspace)) return this.name;

            return this.nspace + "::" + this.name;
        }
        public override int GetHashCode() {
            var hash = 0;
            if (this.name != null) hash ^= this.name.GetHashCode();
            if (this.nspace != null) hash ^= this.nspace.GetHashCode();
            return hash;
        }

        public int CompareTo(Identifier a) {
            var result = String.Compare(this.nspace, a.nspace);
            if (result == 0) return String.Compare(this.name, a.name);
            return result;
        }

    }
    /// <summary>
    /// Identifies a value (literal or expression) used in ASM.
    /// </summary>
    struct AsmValue
    {
        public AsmValue(int value, bool isByte) {
            this.Literal = new LiteralValue((ushort)value, isByte);
            Expression = null;
        }
        public AsmValue(LiteralValue value) {
            this.Literal = value;
            Expression = null;
        }
        public AsmValue(string expression) {
            Literal = new LiteralValue();
            Expression = expression;
        }
        public LiteralValue Literal;
        public string Expression;

        public bool IsExpression { get { return Expression != null; } }
        public bool IsLiteral { get { return !IsExpression; } }
    }
    struct LiteralValue
    {
        public LiteralValue(ushort value, bool isbyte) {
            Value = value;
            IsByte = isbyte;

            // Todo: Is truncation best? Perhaps exception?
            if (isbyte) {
                Value &= 0xFF;
            }
        }
        public readonly ushort Value;
        public readonly bool IsByte;
    }
    struct ParsedInstruction
    {
        public readonly byte opcode;
        public readonly LiteralValue operandValue;
        /// <summary>The expression that specifies the operand value, or null if the expression has been evaluated.</summary>
        public readonly string operandExpression;
        public readonly int sourceLine;

        public ParsedInstruction(byte opcode, LiteralValue operandValue, int sourceLine) {
            this.opcode = opcode;
            this.operandValue = operandValue;
            this.operandExpression = null;
            this.sourceLine = sourceLine;
        }
        public ParsedInstruction(byte opcode, string operandExpression, int sourceLine) {
            this.opcode = opcode;
            this.operandValue = new LiteralValue();
            this.operandExpression = operandExpression;
            this.sourceLine = sourceLine;
        }
        public ParsedInstruction(ParsedInstruction oldLine, byte newOpcode) {
            this = oldLine; // Does this work?
            this.opcode = newOpcode;
        }
        static ParsedInstruction() {
            Empty = new ParsedInstruction(0xFF, null, -1);
        }

        /// <summary>
        /// Defines the value that specifies no value for the OperandValue field
        /// </summary>
        public const int NoLiteralOperand = -1;

        public static readonly ParsedInstruction Empty;
        bool IsEmpty { get { return sourceLine == -1; } }




    }

}
