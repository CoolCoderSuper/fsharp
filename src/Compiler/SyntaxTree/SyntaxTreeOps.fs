// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module FSharp.Compiler.SyntaxTreeOps

open Internal.Utilities.Library
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Features
open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTrivia
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.Xml

/// Generate implicit argument names in parsing
type SynArgNameGenerator() =
    let mutable count = 0
    let generatedArgNamePrefix = "_arg"

    member _.New() : string =
        count <- count + 1
        generatedArgNamePrefix + string count

    member _.Reset() = count <- 0

let ident (s, r) = Ident(s, r)

let textOfId (id: Ident) = id.idText

let pathOfLid lid = List.map textOfId lid

let arrPathOfLid lid = Array.ofList (pathOfLid lid)

let textOfPath path = String.concat "." path

let textOfLid lid = textOfPath (pathOfLid lid)

let rangeOfLid (lid: Ident list) =
    match lid with
    | [] -> failwith "rangeOfLid"
    | [ id ] -> id.idRange
    | h :: t -> unionRanges h.idRange (List.last t).idRange

let mkSynId m s = Ident(s, m)

let pathToSynLid m p = List.map (mkSynId m) p

let mkSynIdGet m n = SynExpr.Ident(mkSynId m n)

let mkSynLidGet m path n =
    let lid = pathToSynLid m path @ [ mkSynId m n ]
    let dots = List.replicate (lid.Length - 1) m
    let trivia: IdentTrivia option list = List.replicate lid.Length None
    SynExpr.LongIdent(false, SynLongIdent(lid, dots, trivia), None, m)

let mkSynIdGetWithAlt m id altInfo =
    match altInfo with
    | None -> SynExpr.Ident id
    | _ -> SynExpr.LongIdent(false, SynLongIdent([ id ], [], [ None ]), altInfo, m)

let mkSynSimplePatVar isOpt id =
    SynSimplePat.Id(id, None, false, false, isOpt, id.idRange)

let mkSynCompGenSimplePatVar id =
    SynSimplePat.Id(id, None, true, false, false, id.idRange)

let rec pushUnaryArg expr arg =
    match expr with
    | SynExpr.App(ExprAtomicFlag.Atomic, infix, SynExpr.Ident ident, x1, m1) ->
        SynExpr.App(
            ExprAtomicFlag.Atomic,
            infix,
            SynExpr.LongIdent(false, SynLongIdent(arg :: [ ident ], [ ident.idRange ], [ None ]), None, ident.idRange),
            x1,
            m1
        )
    | SynExpr.App(ExprAtomicFlag.Atomic,
                  infix,
                  SynExpr.LongIdent(isOptional, SynLongIdent(id, dotRanges, trivia), altNameRefCell, range),
                  x1,
                  m1) ->
        SynExpr.App(
            ExprAtomicFlag.Atomic,
            infix,
            SynExpr.LongIdent(isOptional, SynLongIdent(arg :: id, dotRanges, trivia), altNameRefCell, range),
            x1,
            m1
        )
    | SynExpr.App(ExprAtomicFlag.Atomic, infix, (SynExpr.App(_) as innerApp), x1, m1) ->
        SynExpr.App(ExprAtomicFlag.Atomic, infix, (pushUnaryArg innerApp arg), x1, m1)
    | SynExpr.App(ExprAtomicFlag.Atomic, infix, SynExpr.DotGet(synExpr, rangeOfDot, synLongIdent, range), x1, m1) ->
        SynExpr.App(ExprAtomicFlag.Atomic, infix, SynExpr.DotGet((pushUnaryArg synExpr arg), rangeOfDot, synLongIdent, range), x1, m1)
    | SynExpr.App(ExprAtomicFlag.Atomic, infix, innerExpr, x1, m1) ->
        SynExpr.App(ExprAtomicFlag.Atomic, infix, pushUnaryArg innerExpr arg, x1, m1)
    | SynExpr.Ident ident -> SynExpr.LongIdent(false, SynLongIdent(arg :: [ ident ], [ ident.idRange ], [ None ]), None, ident.idRange)
    | SynExpr.LongIdent(isOptional, SynLongIdent(id, dotRanges, trivia), altNameRefCell, range) ->
        SynExpr.LongIdent(isOptional, SynLongIdent(arg :: id, dotRanges, trivia), altNameRefCell, range)
    | SynExpr.DotGet(synExpr, rangeOfDot, synLongIdent, range) -> SynExpr.DotGet(pushUnaryArg synExpr arg, rangeOfDot, synLongIdent, range)
    | SynExpr.DotIndexedGet(objectExpr, indexArgs, dotRange, range) ->
        SynExpr.DotIndexedGet(pushUnaryArg objectExpr arg, indexArgs, dotRange, range)
    | SynExpr.TypeApp(innerExpr, mLess, tyargs, mCommas, mGreater, mTypars, m) ->
        let innerExpr = pushUnaryArg innerExpr arg
        SynExpr.TypeApp(innerExpr, mLess, tyargs, mCommas, mGreater, mTypars, m)
    | SynExpr.ArbitraryAfterError(_, m) when m.Start = m.End ->
        SynExpr.DiscardAfterMissingQualificationAfterDot(SynExpr.Ident arg, m.StartRange, unionRanges arg.idRange m)
    | SynExpr.DiscardAfterMissingQualificationAfterDot(synExpr, dotRange, m) ->
        SynExpr.DiscardAfterMissingQualificationAfterDot(pushUnaryArg synExpr arg, dotRange, unionRanges arg.idRange m)
    | _ ->
        errorR (Error(FSComp.SR.tcDotLambdaAtNotSupportedExpression (), expr.Range))
        expr

/// CAUTION: This function operates over the untyped tree, so should be used only when absolutely necessary. It doesn't verify assembly origine nor does it respect type aliases.
/// Also, keep in mind that it will only check last part of the assembly (with or without the `Attribute` suffix).
let inline findSynAttribute (attrName: string) (synAttrs: SynAttributes) =
    let attributesToSearch =
        if attrName.EndsWith("Attribute") then
            set [ attrName; attrName.Substring(0, attrName.Length - 9) ]
        else
            set [ attrName; attrName + "Attribute" ]

    synAttrs
    |> List.exists (fun synAttr ->
        synAttr.Attributes
        |> List.exists (fun attr -> attributesToSearch.Contains(attr.TypeName.LongIdent |> List.last |> _.idText)))

[<return: Struct>]
let (|SynSingleIdent|_|) x =
    match x with
    | SynLongIdent([ id ], _, _) -> ValueSome id
    | _ -> ValueNone

/// Match a long identifier, including the case for single identifiers which gets a more optimized node in the syntax tree.
[<return: Struct>]
let (|LongOrSingleIdent|_|) inp =
    match inp with
    | SynExpr.LongIdent(isOpt, lidwd, altId, _m) -> ValueSome(isOpt, lidwd, altId, lidwd.RangeWithoutAnyExtraDot)
    | SynExpr.Ident id -> ValueSome(false, SynLongIdent([ id ], [], [ None ]), None, id.idRange)

    | SynExpr.DiscardAfterMissingQualificationAfterDot(synExpr, dotRange, _) ->
        match synExpr with
        | SynExpr.Ident ident -> ValueSome(false, SynLongIdent([ ident ], [ dotRange ], [ None ]), None, ident.idRange)
        | SynExpr.LongIdent(false, SynLongIdent(idents, dotRanges, trivia), _, range) ->
            ValueSome(false, SynLongIdent(idents, dotRanges @ [ dotRange ], trivia), None, range)
        | _ -> ValueNone

    | _ -> ValueNone

[<return: Struct>]
let (|SingleIdent|_|) inp =
    match inp with
    | SynExpr.LongIdent(false, SynSingleIdent(id), None, _) -> ValueSome id
    | SynExpr.Ident id -> ValueSome id
    | _ -> ValueNone

[<return: Struct>]
let (|SynBinOp|_|) input =
    match input with
    | SynExpr.App(ExprAtomicFlag.NonAtomic,
                  false,
                  SynExpr.App(ExprAtomicFlag.NonAtomic, true, SynExpr.LongIdent(longDotId = SynLongIdent(id = [ synId ])), x1, _m1),
                  x2,
                  _m2) -> ValueSome(synId, x1, x2)
    | _ -> ValueNone

[<return: Struct>]
let (|SynPipeRight|_|) input =
    match input with
    | SynBinOp(synId, x1, x2) when synId.idText = "op_PipeRight" -> ValueSome(x1, x2)
    | _ -> ValueNone

[<return: Struct>]
let (|SynPipeRight2|_|) input =
    match input with
    | SynBinOp(synId, SynExpr.Paren(SynExpr.Tuple(false, [ x1a; x1b ], _, _), _, _, _), x2) when synId.idText = "op_PipeRight2" ->
        ValueSome(x1a, x1b, x2)
    | _ -> ValueNone

[<return: Struct>]
let (|SynPipeRight3|_|) input =
    match input with
    | SynBinOp(synId, SynExpr.Paren(SynExpr.Tuple(false, [ x1a; x1b; x1c ], _, _), _, _, _), x2) when synId.idText = "op_PipeRight3" ->
        ValueSome(x1a, x1b, x1c, x2)
    | _ -> ValueNone

[<return: Struct>]
let (|SynAndAlso|_|) input =
    match input with
    | SynBinOp(synId, x1, x2) when synId.idText = "op_BooleanAnd" -> ValueSome(x1, x2)
    | _ -> ValueNone

[<return: Struct>]
let (|SynOrElse|_|) input =
    match input with
    | SynBinOp(synId, x1, x2) when synId.idText = "op_BooleanOr" -> ValueSome(x1, x2)
    | _ -> ValueNone

/// This affects placement of debug points
let rec IsControlFlowExpression e =
    match e with
    | SynOrElse _
    | SynAndAlso _
    | SynPipeRight _
    | SynPipeRight2 _
    | SynPipeRight3 _
    | SynExpr.MatchLambda _
    | SynExpr.Lambda _
    | SynExpr.LetOrUse _
    | SynExpr.Sequential _
    // Treat "ident { ... }" as a control flow expression
    | SynExpr.App(_, _, SynExpr.Ident _, SynExpr.ComputationExpr _, _)
    | SynExpr.IfThenElse _
    | SynExpr.LetOrUseBang _
    | SynExpr.Match _
    | SynExpr.TryWith _
    | SynExpr.TryFinally _
    | SynExpr.For _
    | SynExpr.ForEach _
    | SynExpr.While _ -> true
    | SynExpr.Typed(e, _, _) -> IsControlFlowExpression e
    | _ -> false

// The syntactic criteria for when a debug point for a 'let' is extended to include
// the 'let' - which happens if we're not defining a function, or a type function,
// and the r.h.s. is not a control-flow expression.  This is a syntactic criteria known
// to both ValidateBreakpointLocation and the parser that is marking up debug points.
//
// For example
//    let x = 1 + 1
// gets extended to include the 'let x'.
//
// A corner case: some things that look like simple value bindings get generalized, e.g.
//    let empty = []
//    let Null = null
// and get compiled as generic methods that return different type instantiations of the given value.
// However these do not have side-effect and do not get a debug point recognised by ValidateBreakpointLocation.

let IsDebugPointBinding synPat synExpr =
    not (IsControlFlowExpression synExpr)
    && let isFunction =
        match synPat with
        | SynPat.LongIdent(argPats = SynArgPats.Pats args; typarDecls = typarDecls) when not args.IsEmpty || typarDecls.IsSome -> true
        | _ -> false in

       not isFunction

let inline unionRangeWithXmlDoc (xmlDoc: PreXmlDoc) range =
    if xmlDoc.IsEmpty then
        range
    else
        unionRanges xmlDoc.Range range

let mkSynAnonField (ty: SynType, xmlDoc) =
    SynField([], false, None, ty, false, xmlDoc, None, unionRangeWithXmlDoc xmlDoc ty.Range, SynFieldTrivia.Zero)

let mkSynNamedField (ident, ty: SynType, xmlDoc, m) =
    SynField([], false, Some ident, ty, false, xmlDoc, None, m, SynFieldTrivia.Zero)

let mkSynPatVar vis (id: Ident) =
    SynPat.Named(SynIdent(id, None), false, vis, id.idRange)

let mkSynThisPatVar (id: Ident) =
    SynPat.Named(SynIdent(id, None), true, None, id.idRange)

let mkSynPatMaybeVar lidwd vis m =
    SynPat.LongIdent(lidwd, None, None, SynArgPats.Pats [], vis, m)

/// Extract the argument for patterns corresponding to the declaration of 'new ... = ...'
[<return: Struct>]
let (|SynPatForConstructorDecl|_|) x =
    match x with
    | SynPat.LongIdent(longDotId = SynSingleIdent _; argPats = SynArgPats.Pats [ arg ]) -> ValueSome arg
    | _ -> ValueNone

/// Recognize the '()' in 'new()'
[<return: Struct>]
let (|SynPatForNullaryArgs|_|) x =
    match x with
    | SynPat.Paren(SynPat.Const(SynConst.Unit, _), _) -> ValueSome()
    | _ -> ValueNone

let (|SynExprErrorSkip|) (p: SynExpr) =
    match p with
    | SynExpr.FromParseError(p, _) -> p
    | _ -> p

[<return: Struct>]
let (|SynExprParen|_|) (e: SynExpr) =
    match e with
    | SynExpr.Paren(SynExprErrorSkip e, a, b, c) -> ValueSome(e, a, b, c)
    | _ -> ValueNone

let (|SynPatErrorSkip|) (p: SynPat) =
    match p with
    | SynPat.FromParseError(p, _) -> p
    | _ -> p

/// Push non-simple parts of a patten match over onto the r.h.s. of a lambda.
/// Return a simple pattern and a function to build a match on the r.h.s. if the pattern is complex
let rec SimplePatOfPat (synArgNameGenerator: SynArgNameGenerator) p =
    match p with
    | SynPat.Typed(p', ty, m) ->
        let p2, laterF = SimplePatOfPat synArgNameGenerator p'
        SynSimplePat.Typed(p2, ty, m), laterF

    | SynPat.Attrib(p', attribs, m) ->
        let p2, laterF = SimplePatOfPat synArgNameGenerator p'
        SynSimplePat.Attrib(p2, attribs, m), laterF

    | SynPat.Named(SynIdent(v, _), thisV, _, m) -> SynSimplePat.Id(v, None, false, thisV, false, m), None

    | SynPat.OptionalVal(v, m) -> SynSimplePat.Id(v, None, false, false, true, m), None

    | SynPat.Paren(p, _) -> SimplePatOfPat synArgNameGenerator p

    | SynPat.FromParseError(p, _) -> SimplePatOfPat synArgNameGenerator p

    | _ ->
        let m = p.Range

        let isCompGen, altNameRefCell, id, item =
            match p with
            | SynPat.LongIdent(longDotId = SynSingleIdent(id); typarDecls = None; argPats = SynArgPats.Pats []; accessibility = None) ->
                // The pattern is 'V' or some other capitalized identifier.
                // It may be a real variable, in which case we want to maintain its name.
                // But it may also be a nullary union case or some other identifier.
                // In this case, we want to use an alternate compiler generated name for the hidden variable.
                let altNameRefCell =
                    Some(ref (SynSimplePatAlternativeIdInfo.Undecided(mkSynId m (synArgNameGenerator.New()))))

                let item = mkSynIdGetWithAlt m id altNameRefCell
                false, altNameRefCell, id, item
            | SynPat.Named(SynIdent(ident, _), _, _, _)
            | SynPat.As(_, SynPat.Named(SynIdent(ident, _), _, _, _), _) ->
                // named pats should be referred to as their name in docs, tooltips, etc.
                let item = mkSynIdGet m ident.idText
                false, None, ident, item
            | _ ->
                let nm = synArgNameGenerator.New()
                let id = mkSynId m nm
                let item = mkSynIdGet m nm
                true, None, id, item

        let fn =
            match p with
            | SynPat.Wild _ -> None
            | _ ->
                Some(fun e ->
                    let clause =
                        SynMatchClause(p, None, e, m, DebugPointAtTarget.No, SynMatchClauseTrivia.Zero)

                    let artificialMatchRange = (unionRanges m e.Range).MakeSynthetic()

                    let trivia =
                        {
                            MatchKeyword = artificialMatchRange
                            WithKeyword = artificialMatchRange
                        }

                    SynExpr.Match(DebugPointAtBinding.NoneAtInvisible, item, [ clause ], artificialMatchRange, trivia))

        SynSimplePat.Id(id, altNameRefCell, isCompGen, false, false, id.idRange), fn

let appFunOpt funOpt x =
    match funOpt with
    | None -> x
    | Some f -> f x

let composeFunOpt funOpt1 funOpt2 =
    match funOpt2 with
    | None -> funOpt1
    | Some f -> Some(fun x -> appFunOpt funOpt1 (f x))

let rec SimplePatsOfPat synArgNameGenerator p =
    match p with
    | SynPat.FromParseError(p, _) -> SimplePatsOfPat synArgNameGenerator p

    | SynPat.Tuple(false, ps, commas, m)

    | SynPat.Paren(SynPat.Tuple(false, ps, commas, _), m) ->
        let sps = List.map (SimplePatOfPat synArgNameGenerator) ps

        let ps2, laterF =
            List.foldBack (fun (p', rhsf) (ps', rhsf') -> p' :: ps', (composeFunOpt rhsf rhsf')) sps ([], None)

        SynSimplePats.SimplePats(ps2, commas, m), laterF

    | SynPat.Paren(SynPat.Const(SynConst.Unit, m), _)

    | SynPat.Const(SynConst.Unit, m) -> SynSimplePats.SimplePats([], [], m), None

    | _ ->
        let m = p.Range
        let sp, laterF = SimplePatOfPat synArgNameGenerator p
        SynSimplePats.SimplePats([ sp ], [], m), laterF

let PushPatternToExpr synArgNameGenerator isMember pat (rhs: SynExpr) =
    let nowPats, laterF = SimplePatsOfPat synArgNameGenerator pat
    nowPats, SynExpr.Lambda(isMember, false, nowPats, appFunOpt laterF rhs, None, rhs.Range, SynExprLambdaTrivia.Zero)

let private isSimplePattern pat =
    let _nowPats, laterF = SimplePatsOfPat (SynArgNameGenerator()) pat
    Option.isNone laterF

/// "fun (UnionCase x) (UnionCase y) -> body"
///       ==>
///   "fun tmp1 tmp2 ->
///        let (UnionCase x) = tmp1 in
///        let (UnionCase y) = tmp2 in
///        body"
let PushCurriedPatternsToExpr synArgNameGenerator wholem isMember pats arrow rhs =
    // Two phases
    // First phase: Fold back, from right to left, pushing patterns into r.h.s. expr
    let spatsl, rhs =
        (pats, ([], rhs))
        ||> List.foldBack (fun arg (spatsl, body) ->
            let spats, bodyf = SimplePatsOfPat synArgNameGenerator arg
            // accumulate the body. This builds "let (UnionCase y) = tmp2 in body"
            let body = appFunOpt bodyf body
            // accumulate the patterns
            let spatsl = spats :: spatsl
            (spatsl, body))
    // Second phase: build lambdas. Mark subsequent ones with "true" indicating they are part of an iterated sequence of lambdas
    let expr =
        match spatsl with
        | [] -> rhs
        | h :: t ->
            let expr =
                List.foldBack (fun spats e -> SynExpr.Lambda(isMember, true, spats, e, None, wholem, { ArrowRange = arrow })) t rhs

            let expr =
                SynExpr.Lambda(isMember, false, h, expr, Some(pats, rhs), wholem, { ArrowRange = arrow })

            expr

    spatsl, expr

let opNameParenGet = CompileOpName parenGet

let opNameQMark = CompileOpName qmark

let mkSynOperator (opm: range) (oper: string) =
    let trivia =
        if
            oper.StartsWithOrdinal("~")
            && ((opm.EndColumn - opm.StartColumn) = (oper.Length - 1))
        then
            // PREFIX_OP token where the ~ was actually absent
            IdentTrivia.OriginalNotation(string (oper.[1..]))
        else
            IdentTrivia.OriginalNotation oper

    SynExpr.LongIdent(false, SynLongIdent([ ident (CompileOpName oper, opm) ], [], [ Some trivia ]), None, opm)

let mkSynInfix opm (l: SynExpr) oper (r: SynExpr) =
    let firstTwoRange = unionRanges l.Range opm
    let mWhole = unionRanges l.Range r.Range

    let app1 =
        SynExpr.App(ExprAtomicFlag.NonAtomic, true, mkSynOperator opm oper, l, firstTwoRange)

    SynExpr.App(ExprAtomicFlag.NonAtomic, false, app1, r, mWhole)

let mkSynBifix m oper x1 x2 =
    let app1 = SynExpr.App(ExprAtomicFlag.NonAtomic, true, mkSynOperator m oper, x1, m)
    SynExpr.App(ExprAtomicFlag.NonAtomic, false, app1, x2, m)

let mkSynTrifix m oper x1 x2 x3 =
    let app1 = SynExpr.App(ExprAtomicFlag.NonAtomic, true, mkSynOperator m oper, x1, m)
    let app2 = SynExpr.App(ExprAtomicFlag.NonAtomic, false, app1, x2, m)
    SynExpr.App(ExprAtomicFlag.NonAtomic, false, app2, x3, m)

let mkSynPrefixPrim opm m oper x =
    SynExpr.App(ExprAtomicFlag.NonAtomic, false, mkSynOperator opm oper, x, m)

let mkSynPrefix opm m oper x =
    if oper = "~&" then SynExpr.AddressOf(true, x, opm, m)
    elif oper = "~&&" then SynExpr.AddressOf(false, x, opm, m)
    else mkSynPrefixPrim opm m oper x

let mkSynCaseName m n = [ mkSynId m (CompileOpName n) ]

let mkSynApp1 f x1 m =
    SynExpr.App(ExprAtomicFlag.NonAtomic, false, f, x1, m)

let mkSynApp2 f x1 x2 m = mkSynApp1 (mkSynApp1 f x1 m) x2 m

let mkSynApp3 f x1 x2 x3 m = mkSynApp1 (mkSynApp2 f x1 x2 m) x3 m

let mkSynApp4 f x1 x2 x3 x4 m = mkSynApp1 (mkSynApp3 f x1 x2 x3 m) x4 m

let mkSynApp5 f x1 x2 x3 x4 x5 m =
    mkSynApp1 (mkSynApp4 f x1 x2 x3 x4 m) x5 m

let mkSynDotParenSet m a b c = mkSynTrifix m parenSet a b c

let mkSynDotBrackGet m mDot a b = SynExpr.DotIndexedGet(a, b, mDot, m)

let mkSynQMarkSet m a b c = mkSynTrifix m qmarkSet a b c

let mkSynDotParenGet mLhs mDot a b =
    match b with
    | SynExpr.Tuple(false, [ _; _ ], _, _) ->
        errorR (Deprecated(FSComp.SR.astDeprecatedIndexerNotation (), mLhs))
        SynExpr.Const(SynConst.Unit, mLhs)

    | SynExpr.Tuple(false, [ _; _; _ ], _, _) ->
        errorR (Deprecated(FSComp.SR.astDeprecatedIndexerNotation (), mLhs))
        SynExpr.Const(SynConst.Unit, mLhs)

    | _ -> mkSynInfix mDot a parenGet b

let mkSynUnit m = SynExpr.Const(SynConst.Unit, m)

let mkSynUnitPat m = SynPat.Const(SynConst.Unit, m)

let mkSynDelay m e =
    let svar = mkSynCompGenSimplePatVar (mkSynId m "unitVar")
    SynExpr.Lambda(false, false, SynSimplePats.SimplePats([ svar ], [], m), e, None, m, SynExprLambdaTrivia.Zero)

let mkSynAssign (l: SynExpr) (r: SynExpr) =
    let m = unionRanges l.Range r.Range

    match l with
    //| SynExpr.Paren (l2, m2)  -> mkSynAssign m l2 r
    | LongOrSingleIdent(false, v, None, _) -> SynExpr.LongIdentSet(v, r, m)
    | SynExpr.DotGet(e, _, v, _) -> SynExpr.DotSet(e, v, r, m)
    | SynExpr.DotIndexedGet(e1, e2, mDot, mLeft) -> SynExpr.DotIndexedSet(e1, e2, r, mLeft, mDot, m)
    | SynExpr.LibraryOnlyUnionCaseFieldGet(x, y, z, _) -> SynExpr.LibraryOnlyUnionCaseFieldSet(x, y, z, r, m)
    | SynExpr.App(_, _, SynExpr.App(_, _, SingleIdent nm, a, _), b, _) when nm.idText = opNameQMark -> mkSynQMarkSet m a b r
    | SynExpr.App(_, _, SynExpr.App(_, _, SingleIdent nm, a, _), b, _) when nm.idText = opNameParenGet -> mkSynDotParenSet m a b r
    | SynExpr.App(_, _, SynExpr.LongIdent(false, v, None, _), x, _) -> SynExpr.NamedIndexedPropertySet(v, x, r, m)
    | SynExpr.App(_, _, SynExpr.DotGet(e, _, v, _), x, _) -> SynExpr.DotNamedIndexedPropertySet(e, v, x, r, m)
    | l -> SynExpr.Set(l, r, m)

let mkSynDot mDot m l (SynIdent(r, rTrivia)) =
    match l with
    | SynExpr.LongIdent(isOpt, SynLongIdent(lid, dots, trivia), None, _) ->
        // REVIEW: MEMORY PERFORMANCE: This list operation is memory intensive (we create a lot of these list nodes)
        SynExpr.LongIdent(isOpt, SynLongIdent(lid @ [ r ], dots @ [ mDot ], trivia @ [ rTrivia ]), None, m)
    | SynExpr.Ident id -> SynExpr.LongIdent(false, SynLongIdent([ id; r ], [ mDot ], [ None; rTrivia ]), None, m)
    | SynExpr.DotGet(e, dm, SynLongIdent(lid, dots, trivia), _) ->
        // REVIEW: MEMORY PERFORMANCE: This is memory intensive (we create a lot of these list nodes)
        SynExpr.DotGet(e, dm, SynLongIdent(lid @ [ r ], dots @ [ mDot ], trivia @ [ rTrivia ]), m)
    | expr -> SynExpr.DotGet(expr, mDot, SynLongIdent([ r ], [], [ rTrivia ]), m)

let mkSynDotMissing (mDot: range) (m: range) (expr: SynExpr) =
    SynExpr.DiscardAfterMissingQualificationAfterDot(expr, mDot, unionRanges mDot m)

let mkSynFunMatchLambdas synArgNameGenerator isMember wholem ps arrow e =
    let _, e = PushCurriedPatternsToExpr synArgNameGenerator wholem isMember ps arrow e
    e

let arbExpr (debugStr, range: range) =
    SynExpr.ArbitraryAfterError(debugStr, range.MakeSynthetic())

let unionRangeWithListBy projectRangeFromThing m listOfThing =
    (m, listOfThing)
    ||> List.fold (fun m thing -> unionRanges m (projectRangeFromThing thing))

let mkAttributeList attrs range : SynAttributeList list =
    [ { Attributes = attrs; Range = range } ]

let ConcatAttributesLists (attrsLists: SynAttributeList list) =
    attrsLists |> List.collect (fun x -> x.Attributes)

let (|Attributes|) synAttributes = ConcatAttributesLists synAttributes

let (|TyparDecls|) (typarDecls: SynTyparDecls option) =
    typarDecls |> Option.map (fun x -> x.TyparDecls) |> Option.defaultValue []

let (|TyparsAndConstraints|) (typarDecls: SynTyparDecls option) =
    typarDecls
    |> Option.map (fun x -> x.TyparDecls, x.Constraints)
    |> Option.defaultValue ([], [])

let (|ValTyparDecls|) (SynValTyparDecls(typarDecls, canInfer)) =
    typarDecls
    |> Option.map (fun x -> x.TyparDecls, x.Constraints, canInfer)
    |> Option.defaultValue ([], [], canInfer)

let rangeOfNonNilAttrs (attrs: SynAttributes) =
    (attrs.Head.Range, attrs.Tail) ||> unionRangeWithListBy (fun a -> a.Range)

let rec stripParenTypes synType =
    match synType with
    | SynType.Paren(innerType, _) -> stripParenTypes innerType
    | _ -> synType

let (|StripParenTypes|) synType = stripParenTypes synType

/// Operations related to the syntactic analysis of arguments of value, function and member definitions and signatures.
module SynInfo =

    /// The argument information for an argument without a name
    let unnamedTopArg1 = SynArgInfo([], false, None)

    /// The argument information for a curried argument without a name
    let unnamedTopArg = [ unnamedTopArg1 ]

    /// The argument information for a '()' argument
    let unitArgData = unnamedTopArg

    /// The 'argument' information for a return value where no attributes are given for the return value (the normal case)
    let unnamedRetVal = SynArgInfo([], false, None)

    /// The 'argument' information for the 'this'/'self' parameter in the cases where it is not given explicitly
    let selfMetadata = unnamedTopArg

    /// Determine if a syntactic information represents a member without arguments (which is implicitly a property getter)
    let HasNoArgs (SynValInfo(args, _)) = isNil args

    /// Check if one particular argument is an optional argument. Used when adjusting the
    /// types of optional arguments for function and member signatures.
    let IsOptionalArg (SynArgInfo(_, isOpt, _)) = isOpt

    /// Check if there are any optional arguments in the syntactic argument information. Used when adjusting the
    /// types of optional arguments for function and member signatures.
    let HasOptionalArgs (SynValInfo(args, _)) =
        List.exists (List.exists IsOptionalArg) args

    /// Add a parameter entry to the syntactic value information to represent the '()' argument to a property getter. This is
    /// used for the implicit '()' argument in property getter signature specifications.
    let IncorporateEmptyTupledArgForPropertyGetter (SynValInfo(args, retInfo)) = SynValInfo([] :: args, retInfo)

    /// Add a parameter entry to the syntactic value information to represent the 'this' argument. This is
    /// used for the implicit 'this' argument in member signature specifications.
    let IncorporateSelfArg (SynValInfo(args, retInfo)) =
        SynValInfo(selfMetadata :: args, retInfo)

    /// Add a parameter entry to the syntactic value information to represent the value argument for a property setter. This is
    /// used for the implicit value argument in property setter signature specifications.
    let IncorporateSetterArg (SynValInfo(args, retInfo)) =
        let args =
            match args with
            | [] -> [ unnamedTopArg ]
            | [ arg ] -> [ arg @ [ unnamedTopArg1 ] ]
            | _ -> failwith "invalid setter type"

        SynValInfo(args, retInfo)

    /// Get the argument counts for each curried argument group. Used in some adhoc places in tc.fs.
    let AritiesOfArgs (SynValInfo(args, _)) = List.map List.length args

    /// Get the argument attributes from the syntactic information for an argument.
    let AttribsOfArgData (SynArgInfo(Attributes attribs, _, _)) = attribs

    /// Infer the syntactic argument info for a single argument from a simple pattern.
    let rec InferSynArgInfoFromSimplePat attribs p =
        match p with
        | SynSimplePat.Id(nm, _, isCompGen, _, isOpt, _) -> SynArgInfo(attribs, isOpt, (if isCompGen then None else Some nm))
        | SynSimplePat.Typed(a, _, _) -> InferSynArgInfoFromSimplePat attribs a
        | SynSimplePat.Attrib(a, attribs2, _) -> InferSynArgInfoFromSimplePat (attribs @ attribs2) a

    /// Infer the syntactic argument info for one or more arguments one or more simple patterns.
    let rec InferSynArgInfoFromSimplePats x =
        match x with
        | SynSimplePats.SimplePats(pats = ps) -> List.map (InferSynArgInfoFromSimplePat []) ps

    /// Infer the syntactic argument info for one or more arguments a pattern.
    let InferSynArgInfoFromPat p =
        // It is ok to use a fresh SynArgNameGenerator here, because compiler generated names are filtered from SynArgInfo, see InferSynArgInfoFromSimplePat above
        let sp, _ = SimplePatsOfPat (SynArgNameGenerator()) p
        InferSynArgInfoFromSimplePats sp

    /// Make sure only a solitary unit argument has unit elimination
    let AdjustArgsForUnitElimination infosForArgs =
        match infosForArgs with
        | [ [] ] -> infosForArgs
        | _ ->
            infosForArgs
            |> List.map (function
                | [] -> unitArgData
                | x -> x)

    /// Transform a property declared using '[static] member P = expr' to a method taking a "unit" argument.
    /// This is similar to IncorporateEmptyTupledArgForPropertyGetter, but applies to member definitions
    /// rather than member signatures.
    let AdjustMemberArgs memFlags infosForArgs =
        match infosForArgs with
        | [] when memFlags = SynMemberKind.Member -> [] :: infosForArgs
        | _ -> infosForArgs

    /// For 'let' definitions, we infer syntactic argument information from the r.h.s. of a definition, if it
    /// is an immediate 'fun ... -> ...' or 'function ...' expression. This is noted in the F# language specification.
    /// This does not apply to member definitions nor to returns with attributes
    let InferLambdaArgs (retInfo: SynArgInfo) origRhsExpr =
        if retInfo.Attributes.Length > 0 then
            []
        else
            let rec loop e =
                match e with
                | SynExpr.Lambda(fromMethod = false; args = spats; body = rest) -> InferSynArgInfoFromSimplePats spats :: loop rest
                | _ -> []

            loop origRhsExpr

    let InferSynReturnData (retInfo: SynReturnInfo option) =
        match retInfo with
        | None -> unnamedRetVal
        | Some(SynReturnInfo((_, retInfo), _)) -> retInfo

    let private emptySynValInfo = SynValInfo([], unnamedRetVal)

    let emptySynValData = SynValData(None, emptySynValInfo, None)

    let emptySynArgInfo = SynArgInfo([], false, None)

    /// Infer the syntactic information for a 'let' or 'member' definition, based on the argument pattern,
    /// any declared return information (e.g. .NET attributes on the return element), and the r.h.s. expression
    /// in the case of 'let' definitions.
    let InferSynValData (memberFlagsOpt: SynMemberFlags option, pat, retInfo, origRhsExpr) =

        let infosForExplicitArgs =
            match pat with
            | Some(SynPat.LongIdent(argPats = SynArgPats.Pats curriedArgs)) -> List.map InferSynArgInfoFromPat curriedArgs
            | _ -> []

        let explicitArgsAreSimple =
            match pat with
            | Some(SynPat.LongIdent(argPats = SynArgPats.Pats curriedArgs)) -> List.forall isSimplePattern curriedArgs
            | _ -> true

        let retInfo = InferSynReturnData retInfo

        match memberFlagsOpt with
        | None ->
            let infosForLambdaArgs = InferLambdaArgs retInfo origRhsExpr

            let infosForArgs =
                infosForExplicitArgs
                @ (if explicitArgsAreSimple then infosForLambdaArgs else [])

            let infosForArgs = AdjustArgsForUnitElimination infosForArgs
            SynValData(None, SynValInfo(infosForArgs, retInfo), None)

        | Some memFlags ->
            let infosForObjArgs = if memFlags.IsInstance then [ selfMetadata ] else []

            let infosForArgs = AdjustMemberArgs memFlags.MemberKind infosForExplicitArgs
            let infosForArgs = AdjustArgsForUnitElimination infosForArgs

            let argInfos = infosForObjArgs @ infosForArgs
            SynValData(Some memFlags, SynValInfo(argInfos, retInfo), None)

let mkSynBindingRhs staticOptimizations rhsExpr mRhs retInfo =
    let rhsExpr =
        List.foldBack (fun (c, e1) e2 -> SynExpr.LibraryOnlyStaticOptimization(c, e1, e2, mRhs)) staticOptimizations rhsExpr

    let rhsExpr, retTyOpt =
        match retInfo with
        | Some(mColon, SynReturnInfo((ty, SynArgInfo(rAttribs, _, _)), tym)) ->
            SynExpr.Typed(rhsExpr, ty, rhsExpr.Range), Some(SynBindingReturnInfo(ty, tym, rAttribs, { ColonRange = mColon }))
        | None -> rhsExpr, None

    rhsExpr, retTyOpt

let mkSynBinding
    (xmlDoc: PreXmlDoc, headPat)
    (vis, isInline, isMutable, mBind, spBind, retInfo, origRhsExpr, mRhs, staticOptimizations, attrs, memberFlagsOpt, trivia)
    =
    let info =
        SynInfo.InferSynValData(memberFlagsOpt, Some headPat, Option.map snd retInfo, origRhsExpr)

    let rhsExpr, retTyOpt = mkSynBindingRhs staticOptimizations origRhsExpr mRhs retInfo
    let mBind = unionRangeWithXmlDoc xmlDoc mBind
    SynBinding(vis, SynBindingKind.Normal, isInline, isMutable, attrs, xmlDoc, info, headPat, retTyOpt, rhsExpr, mBind, spBind, trivia)

let NonVirtualMemberFlags k : SynMemberFlags =
    {
        MemberKind = k
        IsInstance = true
        IsDispatchSlot = false
        IsOverrideOrExplicitImpl = false
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let CtorMemberFlags: SynMemberFlags =
    {
        MemberKind = SynMemberKind.Constructor
        IsInstance = false
        IsDispatchSlot = false
        IsOverrideOrExplicitImpl = false
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let ClassCtorMemberFlags: SynMemberFlags =
    {
        MemberKind = SynMemberKind.ClassConstructor
        IsInstance = false
        IsDispatchSlot = false
        IsOverrideOrExplicitImpl = false
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let OverrideMemberFlags k : SynMemberFlags =
    {
        MemberKind = k
        IsInstance = true
        IsDispatchSlot = false
        IsOverrideOrExplicitImpl = true
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let AbstractMemberFlags isInstance k : SynMemberFlags =
    {
        MemberKind = k
        IsInstance = isInstance
        IsDispatchSlot = true
        IsOverrideOrExplicitImpl = false
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let StaticMemberFlags k : SynMemberFlags =
    {
        MemberKind = k
        IsInstance = false
        IsDispatchSlot = false
        IsOverrideOrExplicitImpl = false
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let ImplementStaticMemberFlags k : SynMemberFlags =
    {
        MemberKind = k
        IsInstance = false
        IsDispatchSlot = false
        IsOverrideOrExplicitImpl = true
        IsFinal = false
        GetterOrSetterIsCompilerGenerated = false
    }

let inferredTyparDecls = SynValTyparDecls(None, true)

let noInferredTypars = SynValTyparDecls(None, false)

let unionBindingAndMembers (bindings: SynBinding list) (members: SynMemberDefn list) : SynBinding list =
    [
        yield! bindings
        yield!
            List.choose
                (function
                | SynMemberDefn.Member(b, _) -> Some b
                | _ -> None)
                members
    ]

let rec synExprContainsError inpExpr =
    let rec walkBind (SynBinding(expr = synExpr)) = walkExpr synExpr

    and walkExprs es = es |> List.exists walkExpr

    and walkBinds es = es |> List.exists walkBind

    and walkMatchClauses cl =
        cl
        |> List.exists (fun (SynMatchClause(whenExpr = whenExpr; resultExpr = e)) -> walkExprOpt whenExpr || walkExpr e)

    and walkExprOpt eOpt = eOpt |> Option.exists walkExpr

    and walkExpr e =
        match e with
        | SynExpr.FromParseError _
        | SynExpr.DiscardAfterMissingQualificationAfterDot _
        | SynExpr.ArbitraryAfterError _ -> true

        | SynExpr.LongIdent _
        | SynExpr.DotLambda _
        | SynExpr.Quote _
        | SynExpr.LibraryOnlyILAssembly _
        | SynExpr.LibraryOnlyStaticOptimization _
        | SynExpr.Null _
        | SynExpr.Ident _
        | SynExpr.Typar _
        | SynExpr.ImplicitZero _
        | SynExpr.Const _
        | SynExpr.Dynamic _ -> false

        | SynExpr.TypeTest(e, _, _)
        | SynExpr.Upcast(e, _, _)
        | SynExpr.AddressOf(_, e, _, _)
        | SynExpr.ComputationExpr(_, e, _)
        | SynExpr.ArrayOrListComputed(_, e, _)
        | SynExpr.Typed(e, _, _)
        | SynExpr.Do(e, _)
        | SynExpr.Assert(e, _)
        | SynExpr.DotGet(e, _, _, _)
        | SynExpr.LongIdentSet(_, e, _)
        | SynExpr.New(_, _, e, _)
        | SynExpr.TypeApp(e, _, _, _, _, _, _)
        | SynExpr.LibraryOnlyUnionCaseFieldGet(e, _, _, _)
        | SynExpr.Downcast(e, _, _)
        | SynExpr.InferredUpcast(e, _)
        | SynExpr.InferredDowncast(e, _)
        | SynExpr.Lazy(e, _)
        | SynExpr.TraitCall(_, _, e, _)
        | SynExpr.YieldOrReturn(_, e, _, _)
        | SynExpr.YieldOrReturnFrom(_, e, _, _)
        | SynExpr.DoBang(e, _, _)
        | SynExpr.Fixed(e, _)
        | SynExpr.DebugPoint(_, _, e)
        | SynExpr.Paren(e, _, _, _) -> walkExpr e

        | SynExpr.NamedIndexedPropertySet(_, e1, e2, _)
        | SynExpr.DotSet(e1, _, e2, _)
        | SynExpr.Set(e1, e2, _)
        | SynExpr.LibraryOnlyUnionCaseFieldSet(e1, _, _, e2, _)
        | SynExpr.JoinIn(e1, _, e2, _)
        | SynExpr.App(_, _, e1, e2, _) -> walkExpr e1 || walkExpr e2

        | SynExpr.ArrayOrList(_, es, _)
        | SynExpr.Tuple(_, es, _, _) -> walkExprs es

        | SynExpr.AnonRecd(copyInfo = origExpr; recordFields = flds) ->
            (match origExpr with
             | Some(e, _) -> walkExpr e
             | None -> false)
            || walkExprs (List.map (fun (_, _, e) -> e) flds)

        | SynExpr.Record(_, origExpr, fs, _) ->
            (match origExpr with
             | Some(e, _) -> walkExpr e
             | None -> false)
            || (let flds = fs |> List.choose (fun (SynExprRecordField(expr = v)) -> v)
                walkExprs flds)

        | SynExpr.ObjExpr(bindings = bs; members = ms; extraImpls = is) ->
            let bs = unionBindingAndMembers bs ms

            let binds =
                [
                    for SynInterfaceImpl(bindings = bs) in is do
                        yield! bs
                ]

            walkBinds bs || walkBinds binds

        | SynExpr.ForEach(_, _, _, _, _, e1, e2, _)
        | SynExpr.While(_, e1, e2, _)
        | SynExpr.WhileBang(_, e1, e2, _) -> walkExpr e1 || walkExpr e2

        | SynExpr.For(identBody = e1; toBody = e2; doBody = e3) -> walkExpr e1 || walkExpr e2 || walkExpr e3

        | SynExpr.MatchLambda(_, _, cl, _, _) -> walkMatchClauses cl

        | SynExpr.Lambda(body = e) -> walkExpr e

        | SynExpr.Match(expr = e; clauses = cl)
        | SynExpr.MatchBang(expr = e; clauses = cl) -> walkExpr e || walkMatchClauses cl

        | SynExpr.LetOrUse(bindings = bs; body = e) -> walkBinds bs || walkExpr e

        | SynExpr.TryWith(tryExpr = e; withCases = cl) -> walkExpr e || walkMatchClauses cl

        | SynExpr.TryFinally(tryExpr = e1; finallyExpr = e2) -> walkExpr e1 || walkExpr e2

        | SynExpr.Sequential(expr1 = e1; expr2 = e2) -> walkExpr e1 || walkExpr e2

        | SynExpr.SequentialOrImplicitYield(_, e1, e2, _, _) -> walkExpr e1 || walkExpr e2

        | SynExpr.IfThenElse(ifExpr = e1; thenExpr = e2; elseExpr = e3opt) -> walkExpr e1 || walkExpr e2 || walkExprOpt e3opt

        | SynExpr.IndexRange(expr1, _, expr2, _, _, _) ->
            (match expr1 with
             | Some e -> walkExpr e
             | None -> false)
            || (match expr2 with
                | Some e -> walkExpr e
                | None -> false)

        | SynExpr.IndexFromEnd(e, _) -> walkExpr e

        | SynExpr.DotIndexedGet(e1, indexArgs, _, _) -> walkExpr e1 || walkExpr indexArgs

        | SynExpr.DotIndexedSet(e1, indexArgs, e2, _, _, _) -> walkExpr e1 || walkExpr indexArgs || walkExpr e2

        | SynExpr.DotNamedIndexedPropertySet(e1, _, e2, e3, _) -> walkExpr e1 || walkExpr e2 || walkExpr e3

        | SynExpr.LetOrUseBang(rhs = e1; body = e2; andBangs = es) ->
            walkExpr e1
            || walkExprs
                [
                    for SynExprAndBang(body = e) in es do
                        yield e
                ]
            || walkExpr e2

        | SynExpr.InterpolatedString(parts, _, _m) ->
            parts
            |> List.choose (function
                | SynInterpolatedStringPart.String _ -> None
                | SynInterpolatedStringPart.FillExpr(x, _) -> Some x)
            |> walkExprs

    walkExpr inpExpr

let longIdentToString (ident: SynLongIdent) =
    System.String.Join(".", ident.LongIdent |> List.map (fun ident -> ident.idText.ToString()))

let parsedHashDirectiveArguments (input: ParsedHashDirectiveArgument list) (langVersion: LanguageVersion) =
    List.choose
        (function
        | ParsedHashDirectiveArgument.String(s, _, _) -> Some s
        | ParsedHashDirectiveArgument.SourceIdentifier(_, v, _) -> Some v
        | ParsedHashDirectiveArgument.Int32(n, m) ->
            match tryCheckLanguageFeatureAndRecover langVersion LanguageFeature.ParsedHashDirectiveArgumentNonQuotes m with
            | true -> Some(string n)
            | false -> None
        | ParsedHashDirectiveArgument.Ident(ident, m) ->
            match tryCheckLanguageFeatureAndRecover langVersion LanguageFeature.ParsedHashDirectiveArgumentNonQuotes m with
            | true -> Some(ident.idText)
            | false -> None
        | ParsedHashDirectiveArgument.LongIdent(ident, m) ->
            match tryCheckLanguageFeatureAndRecover langVersion LanguageFeature.ParsedHashDirectiveArgumentNonQuotes m with
            | true -> Some(longIdentToString ident)
            | false -> None)
        input

let parsedHashDirectiveArgumentsNoCheck (input: ParsedHashDirectiveArgument list) =
    List.map
        (function
        | ParsedHashDirectiveArgument.String(s, _, _) -> s
        | ParsedHashDirectiveArgument.SourceIdentifier(_, v, _) -> v
        | ParsedHashDirectiveArgument.Int32(n, _) -> string n
        | ParsedHashDirectiveArgument.Ident(ident, _) -> ident.idText
        | ParsedHashDirectiveArgument.LongIdent(ident, _) -> longIdentToString ident)
        input

let parsedHashDirectiveStringArguments (input: ParsedHashDirectiveArgument list) (_langVersion: LanguageVersion) =
    List.choose
        (function
        | ParsedHashDirectiveArgument.String(s, _, _) -> Some s
        | ParsedHashDirectiveArgument.Int32(n, m) ->
            errorR (Error(FSComp.SR.featureParsedHashDirectiveUnexpectedInteger (n), m))
            None
        | ParsedHashDirectiveArgument.SourceIdentifier(_, v, _) -> Some v
        | ParsedHashDirectiveArgument.Ident(ident, m) ->
            errorR (Error(FSComp.SR.featureParsedHashDirectiveUnexpectedIdentifier (ident.idText), m))
            None
        | ParsedHashDirectiveArgument.LongIdent(ident, m) ->
            errorR (Error(FSComp.SR.featureParsedHashDirectiveUnexpectedIdentifier (longIdentToString ident), m))
            None)
        input

let prependIdentInLongIdentWithTrivia (SynIdent(ident, identTrivia)) mDot lid =
    match lid with
    | SynLongIdent(lid, dots, trivia) -> SynLongIdent(ident :: lid, mDot :: dots, identTrivia :: trivia)

let mkDynamicArgExpr expr =
    match expr with
    | SynExpr.Ident ident ->
        let con = SynConst.String(ident.idText, SynStringKind.Regular, ident.idRange)
        SynExpr.Const(con, con.Range ident.idRange)
    | SynExpr.Paren(expr = e) -> e
    | e -> e

let rec normalizeTuplePat pats commas : SynPat list * range List =
    match pats with
    | SynPat.Tuple(false, innerPats, innerCommas, _) :: rest ->
        let innerPats, innerCommas =
            normalizeTuplePat (List.rev innerPats) (List.rev innerCommas)

        innerPats @ rest, innerCommas @ commas
    | _ -> pats, commas

/// Remove all members that were captures as SynMemberDefn.GetSetMember
let rec desugarGetSetMembers (memberDefns: SynMemberDefns) =
    memberDefns
    |> List.collect (fun md ->
        match md with
        | SynMemberDefn.GetSetMember(Some(SynBinding _ as getBinding),
                                     Some(SynBinding _ as setBinding),
                                     m,
                                     {
                                         GetKeyword = Some mGet
                                         SetKeyword = Some mSet
                                     }) ->
            if Position.posLt mGet.Start mSet.Start then
                [ SynMemberDefn.Member(getBinding, m); SynMemberDefn.Member(setBinding, m) ]
            else
                [ SynMemberDefn.Member(setBinding, m); SynMemberDefn.Member(getBinding, m) ]
        | SynMemberDefn.GetSetMember(Some binding, None, m, _)
        | SynMemberDefn.GetSetMember(None, Some binding, m, _) -> [ SynMemberDefn.Member(binding, m) ]
        | SynMemberDefn.Interface(interfaceType, withKeyword, members, m) ->
            let members = Option.map desugarGetSetMembers members
            [ SynMemberDefn.Interface(interfaceType, withKeyword, members, m) ]
        | md -> [ md ])

let getTypeFromTuplePath (path: SynTupleTypeSegment list) : SynType list =
    path
    |> List.choose (function
        | SynTupleTypeSegment.Type t -> Some t
        | _ -> None)

[<return: Struct>]
let (|MultiDimensionArrayType|_|) (t: SynType) =
    match t with
    | SynType.App(StripParenTypes(SynType.LongIdent(SynLongIdent([ identifier ], _, _))), _, [ elementType ], _, _, true, m) ->
        if System.Text.RegularExpressions.Regex.IsMatch(identifier.idText, "^array\d\d?d$") then
            let rank =
                identifier.idText
                |> Seq.filter System.Char.IsDigit
                |> Seq.toArray
                |> System.String
                |> int

            ValueSome(rank, elementType, m)
        else
            ValueNone
    | _ -> ValueNone

let (|TypesForTypar|) (t: SynType) =
    let rec visit continuation t =
        match t with
        | SynType.Paren(innerT, _) -> visit continuation innerT
        | SynType.Or(lhsT, rhsT, _, _) -> visit (fun lhsTs -> [ yield! lhsTs; yield rhsT ] |> continuation) lhsT
        | _ -> continuation [ t ]

    visit id t

[<return: Struct>]
let (|Get_OrSet_Ident|_|) (ident: Ident) =
    if ident.idText.StartsWithOrdinal("get_") then ValueSome()
    elif ident.idText.StartsWithOrdinal("set_") then ValueSome()
    else ValueNone

let getGetterSetterAccess synValSigAccess memberKind (langVersion: Features.LanguageVersion) =
    match synValSigAccess with
    | SynValSigAccess.Single(access) -> access, access
    | SynValSigAccess.GetSet(access, getterAccess, setterAccess) ->
        let checkAccess (access: SynAccess option) (accessBeforeGetSet: SynAccess option) =
            match accessBeforeGetSet, access with
            | None, _ -> access
            | Some x, Some _ ->
                errorR (Error(FSComp.SR.parsMultipleAccessibilitiesForGetSet (), x.Range))
                None
            | Some x, None ->
                checkLanguageFeatureAndRecover
                    langVersion
                    Features.LanguageFeature.AllowAccessModifiersToAutoPropertiesGettersAndSetters
                    x.Range

                accessBeforeGetSet

        match memberKind with
        | SynMemberKind.PropertyGetSet ->
            match access, (getterAccess, setterAccess) with
            | _, (None, None) -> access, access
            | None, (Some x, _)
            | None, (_, Some x) ->
                checkLanguageFeatureAndRecover
                    langVersion
                    Features.LanguageFeature.AllowAccessModifiersToAutoPropertiesGettersAndSetters
                    x.Range

                getterAccess, setterAccess
            | _, (Some x, _)
            | _, (_, Some x) ->
                errorR (Error(FSComp.SR.parsMultipleAccessibilitiesForGetSet (), x.Range))
                None, None

        | SynMemberKind.PropertySet -> None, checkAccess access setterAccess
        | SynMemberKind.Member
        | SynMemberKind.PropertyGet
        | _ -> checkAccess access getterAccess, None

let addEmptyMatchClause (mBar1: range) (mBar2: range) (clauses: SynMatchClause list) =
    let rec addOrPat (pat: SynPat) =
        match pat with
        | SynPat.As(lhsPat, rhsPat, range) -> SynPat.As(addOrPat lhsPat, rhsPat, range)
        | _ ->
            let mPat1 = mBar1.EndRange
            let pat1 = SynPat.Wild(mPat1)
            SynPat.Or(pat1, pat, unionRanges mPat1 pat.Range, { BarRange = mBar2 })

    match clauses with
    | [] -> []
    | SynMatchClause(pat, whenExpr, resultExpr, range, debugPoint, trivia) :: restClauses ->
        SynMatchClause(addOrPat pat, whenExpr, resultExpr, range, debugPoint, trivia)
        :: restClauses
