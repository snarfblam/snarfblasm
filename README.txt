snarfblASM 6502 Assembler
  by snarfblam
  
Version 1.1
http://snarfblam.com/words  
snarfblam@snarfblam.com
  
Command Line ----------------------------------------------------------------

snarfblasm sourceFile [destFile] [switches]
    switches:
        -CHECKING:OFF/ON/SIGNED
            Overflow checking in expressions
        -OFFSET:value
            value should be a decimal, $hex, or 0xhex offset to patch the dest file
        -DOT[:OFF/ON]
            Optional dots are enabled for directives (ON)
        -COLON[:OFF/ON]
            Optional colons are enabled for labels (ON)
        -ASM6[:OFF/ON]
            ASM6-like syntax (same as -DOT:ON -COLON:ON)
        -INVALID[:OFF/ON]
            Invalid opcodes are allowed (ON)
        -IPS[:OFF/ON]
            Output IPS format (ON)
            
Syntax ----------------------------------------------------------------------

The snarfblASM syntax is pretty much standard 6502 assembler syntax. Comments start with a semi-colon. Directives should be preceeded by a dot (unless the -d switch is specified). A label may appear on it's own line, or preceeding an instruction or directive. There can be multiple labels on a line.

    * minus: - -- --- plus: + ++ +++ LDA --   ; Legal. Still, don't do it.
    
The syntax for the various addressing modes is listed below. Note that for instructions that operate on the accumulator, it is not necessary to specify A as an operand, although it is allowed. snarfblASM treats accumulator addressing as a case of implied addressing.
    Implied                     CLC
    Accumulator                 ROR
                                ROR A
    Immediate                   LDA #$FF
    Absolute                    LDA $FFFF
    Absolute, X-indexed         LDA $C080, X
    Absolute, Y-indexed         LDA $C080, Y
    Zero-page                   LDA $FF
    Zero-page, X-indexed        LDA $80, X
    Zero-page, Y-indexed        LDA $80, Y
    Indirect                    JMP ($0000)
    Indirect, X-pre-indexed     LDA ($00, X)
    Indirect, Y-post-indexed    LDA ($00), Y

Square brackets are not supported for indexing. Including an entire expression in parentheses will cause an instruction to be interpreted as indirect addressing. However, to include an entire expression within parentheses for clarity, the unary "+" operator (effectively a no-operation) can be used to disambiguate, since it causes the expression to extend beyond the parentheses.
    LDA   someVar + someOtherVar / 2, Y     ; Y-indexed
    LDA  (someVar + someOtherVar / 2), Y    ; Indirect, Y-indexed
    LDA +(sameVar + someOtherVar / 2), Y    ; Y-Indexed

      
Labels, Variables, And Expressions ------------------------------------------

snarfblASM supports the use of assembler variables, or named values. Labels are a special case of assembler variables. Variables can also be explicitly declared.
    someAddress = $A000
These variables can then be used in expressions (operators are listed below). Variables and expressions can also be used as operands to instructions.
    LDA someAddress + 1
    someOtherAddress = someAddress + $1000

A variable can't be used within an expression until after it has been declared. Labels are an exception and can be referenced before the label occurs.

snarfblASM works with two kinds of values: 8-bit values (byte) and 16-bit values (referred to as "word", though, technically, the word size for 6502 is 8-bit). The byte versus word distinction can be important in certain circumstances. Adding two 8-bit values produces an 8-bit value, and truncation may occur (or an overflow error if the -o or -os switch is specified). Typically, when an operator is used on a byte and a word, the result is a word.
    var = $FF + $10     ; Results in $0F
    var = $FF + $0010   ; Results in $010F
snarfblASM decides whether to use absolute addressing or zero-page addressing based on whether the operand is an 8-bit or 16-bit value. To be positive that zero-page addressing is used when an expression or variable is used as an operand, the low-byte ("<") operator can be used. To ensure that absolute addressing is used, the widen ("<>") operator may be used.
    var = $80
    lda var      ; Zero-page
    lda <>var    ; Absolute
    var = $0080
    lda var      ; Absolute
    lda <var     ; Zero-page
Labels are typically word values. However, if a label is referenced after it occurs, and the label's address is less than $100, then the label will be treated as a byte value. This allows pre-declared zero-page labels to be used.
    .enum $0000         ; Zero-page variables
        Ptr0:   .dsw
        Ptr2:   .dsw
    .endenum
    .org $8000
    LDA Ptr0            ; Zero-page addressing
Two styles of anonymous labels are supported: ASM6-style + and - labels, and Ophis-style * labels. Mixing the is probably a bad plan, but supported. In the event that both are used, the closest matching anonymous label is used. Named anonymous labels are not currently supported. Anonymous labels always act as word values.
            LDA ++ ; Loads $04
        +++ .db $01
        *   .db $02
        +   .db $03
        *   .db $04 ; Closest Match
        ++  .db $05 ; Further Match
    
Expression Operators --------------------------------------------------------
    Listed by precedence:
        ++ - (Unary, Post) Increment 
        -- - (Unary, Post) Decrement
        
        -  - (Unary) Negate
        +  - (Unary) Effectively a NOP
        ~  - (Unary) Binary Not
        !  - (Unary) Boolean Not
        <  - (Unary) Lower Byte
        >  - (Unary) Upper Byte
        <> - (Unary) Widen to word
        ++ - (Unary, Pre) Increment
        -- - (Unary, Pre) Decrement
        
        &  - Binary And
        |  - Binary Or
        ^  - Binary Xor
        
        << - Shift left
        >> - Shift right
        
        *  - Multiply
        /  - Divide
        %  - Modulus
        
        +  - Add
        -  - Subtract
        
        == - Compare (equal)
        != - Compare (inequal)
        <> - Compare (inequal)
        <= - Compare (less than or equal)
        >= - Compare (greater than or equal)
        <  - Compare (less than)
        >  - Compare (greater than)
        
        && - Boolean And
        || - Boolean Or
        ^^ - Boolean Xor

Boolean and comparison operators result in a byte value of $00 (false) or $FF (true), though any value other than $00 is also considered true. For example, $03 || $00 results in $FF. Shift operators return a byte or word, depending on the left-hand operand. All other binary operators return a value the same width (byte or word) as the widest operand. For example, $7F | $0010 results in (word) $008F. 

Directives ------------------------------------------------------------------

.alias
    Behaves identically to normal value declaration. For example, the following have the same effect. 
        .alias valueName $FF
        valueName = $FF
    Expressions are supported.
.org
    The first .org (or .base) specifies the starting address. Subsequent .org directives will pad to the specified address with zeros. Specifying an address less than the current address is an error.
.base
    Sets the output address.
.db, .byte
    Writes a byte value or a list of byte values to the output. An expression may be used for each value.
.dw, .word
    Writes a word value or a list of word values to the output. An expression may be used for each value.
.data
    Writes a value or list of values to the output. Values may be bytes or words. For example:
        ; Produces the output {10 30 20 05 80}
        .org $8000
        .data $10, $2030, someLabel
        someLabel:

.enum
    Surpresses output. Useful for declaring variables in RAM. Close a .enum block with .ende or .endenum.
        .enum $0000  ; Zero page variables
            Ptr0:     .dw
            Ptr2:     .dw
            GameMode: .db
        .endenum
    Note that, while you can reference a label before you declare it, in order to use a zero-page instruction with a label, the label must be declared before using it as an operand.
.dsb
    Inserts storage bytes (useful to reserve addresses for variables within an .enum). By default, one byte with a value of $00 is inserted. A byte count can be specified, optionally followed by a fill value.
        .dsb            ; emit 1  $00
        .dsb $10        ; emit 16 $00
        .dsb $20, $FF   ; emit 32 $FF
.dsw
    Inserts storage words (useful to reserve addresses for variables within an .enum). By default, one word with a value of $0000 is inserted. A word count can be specified, optionally followed by a fill value.
        .dsw            ; emit 1  $0000
        .dsw $10        ; emit 16 $0000
        .dsw $20, $BEEF ; emit 32 $BEEF
        .dsw $40, $48   ; emit 64 $0048
        
.define
    Defines a symbol. This is to be used for .ifdef statements.
        .define DEBUG
.if
    Causes code to assemble only if a specified condition is met. .if directives should NOT refer to or depend on labels that occur later in code. Must be followed by .endif.
        .if $ > $BFFF
            .error  Too much to fit in bank!
        .endif
    An .if block can also contain .elseif and .else clauses.
.ifdef
    Causes code to assemble if the specified symbol is defined. Must be followed by .endif.
.ifndef
    Causes code to assemble if the specified symbol is not defined. Must be followed by .endif.

.include
    Includes a separate ASM file.
.incbin
    Inserts a binary file in the output.
    
.error
    Produces an error. Useful with directives such as .if to identify problematic conditions.
        beforeIncBin:
        .incbin "somefile"
        afterIncBin:
        includedFileSize = afterIncBin - beforeIncBin
        
        .if includedFileSize > $1000
            .error  "somefile" too large ; Assembler will fail with specified error message
        .endif
                
.overflow
    Specify an option, "ON" or "OFF". This option is "OFF" by default. If .overflow is enabled, any expressions that produce an overflow will result in an assembler error. For example:
        value_A = $80 * $2 ; Results in 0
        .overflow ON
        value_B = $80 * $2 ; Produces an error
.signed
    Specify an option, "ON" or "OFF". This option is "OFF" by default. This option is used in conjunction with .overflow. If .signed is enabled, overflow detection will operate in signed mode, giving a range of -128 to 127 for bytes, and -32768 to 32767 for words. For example:
        .overflow ON
        value_A = $FF ;
        value_B = $10 ;
        .signed ON
        value_C = value_A + value_B  ; (-1 + 16 = 15, or $F)
        .signed OFF
        value_D = value_A + value_B  ; ERROR! (255 + 16 = 271, or $10F)
        

Invalid Opcodes ------------------------------------------------------------

    Invalid opcodes, those that are not part of the proper 6502 specification, can be used if the -i command line switch is used. This allows for unsupported instructions. For example, LAX #$55 (load A and X with #$55) would normally result in an error, but if the -i switch is used, the code will assemble.
    
    
Unsupported Features -------------------------------------------------------

    The following features are not currently supported.
        -Macros
        -Tables or non-ASCII string literals