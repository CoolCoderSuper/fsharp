// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module FSharp.Compiler.DiagnosticsLogger

open FSharp.Compiler
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Features
open FSharp.Compiler.Text.Range
open FSharp.Compiler.Text
open System
open System.Diagnostics
open System.Reflection
open System.Threading
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Internal.Utilities.Library
open Internal.Utilities.Library.Extras
open System.Threading.Tasks

/// Represents the style being used to format errors
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type DiagnosticStyle =
    | Default
    | Emacs
    | Test
    | VisualStudio
    | Gcc
    | Rich

/// Thrown when we want to add some range information to a .NET exception
exception WrappedError of exn * range with
    override this.Message =
        match this :> exn with
        | WrappedError(exn, _) -> "WrappedError(" + exn.Message + ")"
        | _ -> "WrappedError"

/// Thrown when immediate, local error recovery is not possible. This indicates
/// we've reported an error but need to make a non-local transfer of control.
/// Error recovery may catch this and continue (see 'errorRecovery')
///
/// The exception that caused the report is carried as data because in some
/// situations (LazyWithContext) we may need to re-report the original error
/// when a lazy thunk is re-evaluated.
exception ReportedError of exn option with
    override this.Message =
        let msg =
            "The exception has been reported. This internal exception should now be caught at an error recovery point on the stack."

        match this :> exn with
        | ReportedError(Some exn) -> msg + " Original message: " + exn.Message + ")"
        | _ -> msg

let rec findOriginalException err =
    match err with
    | ReportedError(Some err) -> err
    | WrappedError(err, _) -> findOriginalException err
    | _ -> err

type Suggestions = (string -> unit) -> unit

let NoSuggestions: Suggestions = ignore

/// Thrown when we stop processing the F# Interactive entry or #load.
exception StopProcessingExn of exn option with
    override _.Message =
        "Processing of a script fragment has stopped because an exception has been raised"

    override this.ToString() =
        match this :> exn with
        | StopProcessingExn(Some exn) -> "StopProcessingExn, originally (" + exn.ToString() + ")"
        | _ -> "StopProcessingExn"

[<return: Struct>]
let (|StopProcessing|_|) exn =
    match exn with
    | StopProcessingExn _ -> ValueSome()
    | _ -> ValueNone

let StopProcessing<'T> = StopProcessingExn None

// int is e.g. 191 in FS0191
exception DiagnosticWithText of number: int * message: string * range: range with
    override this.Message =
        match this :> exn with
        | DiagnosticWithText(_, msg, _) -> msg
        | _ -> "impossible"

exception InternalError of message: string * range: range with
    override this.Message =
        match this :> exn with
        | InternalError(msg, m) -> msg + m.ToString()
        | _ -> "impossible"

exception InternalException of exn: Exception * msg: string * range: range with
    override this.Message =
        match this :> exn with
        | InternalException(_, msg, _) -> msg
        | _ -> "impossible"

    override this.ToString() =
        match this :> exn with
        | InternalException(exn, _, _) -> exn.ToString()
        | _ -> "impossible"

exception UserCompilerMessage of message: string * number: int * range: range

exception LibraryUseOnly of range: range

exception Deprecated of message: string * range: range

exception Experimental of message: string option * diagnosticId: string option * urlFormat: string option * range: range

exception PossibleUnverifiableCode of range: range

exception UnresolvedReferenceNoRange of assemblyName: string

exception UnresolvedReferenceError of assemblyName: string * range: range

exception UnresolvedPathReferenceNoRange of assemblyName: string * path: string with
    override this.Message =
        match this :> exn with
        | UnresolvedPathReferenceNoRange(assemblyName, path) -> sprintf "Assembly: %s, full path: %s" assemblyName path
        | _ -> "impossible"

exception UnresolvedPathReference of assemblyName: string * path: string * range: range

exception DiagnosticWithSuggestions of number: int * message: string * range: range * identifier: string * suggestions: Suggestions with // int is e.g. 191 in FS0191
    override this.Message =
        match this :> exn with
        | DiagnosticWithSuggestions(_, msg, _, _, _) -> msg
        | _ -> "impossible"

/// A diagnostic that is raised when enabled manually, or by default with a language feature
exception DiagnosticEnabledWithLanguageFeature of number: int * message: string * range: range * enabledByLangFeature: bool

/// A diagnostic that is raised when a diagnostic is obsolete
exception ObsoleteDiagnostic of
    isError: bool *
    diagnosticId: string option *
    message: string option *
    urlFormat: string option *
    range: range

/// The F# compiler code currently uses 'Error(...)' in many places to create
/// an DiagnosticWithText as an exception even if it's a warning.
///
/// We will eventually rename this to remove this use of "Error"
let Error ((n, text), m) = DiagnosticWithText(n, text, m)

/// The F# compiler code currently uses 'ErrorWithSuggestions(...)' in many places to create
/// an DiagnosticWithText as an exception even if it's a warning.
///
/// We will eventually rename this to remove this use of "Error"
let ErrorWithSuggestions ((n, message), m, id, suggestions) =
    DiagnosticWithSuggestions(n, message, m, id, suggestions)

let ErrorEnabledWithLanguageFeature ((n, message), m, enabledByLangFeature) =
    DiagnosticEnabledWithLanguageFeature(n, message, m, enabledByLangFeature)

let inline protectAssemblyExploration dflt ([<InlineIfLambda>] f) =
    try
        f ()
    with
    | UnresolvedPathReferenceNoRange _ -> dflt
    | _ -> reraise ()

let inline protectAssemblyExplorationF dflt ([<InlineIfLambda>] f) =
    try
        f ()
    with
    | UnresolvedPathReferenceNoRange(asmName, path) -> dflt (asmName, path)
    | _ -> reraise ()

let inline protectAssemblyExplorationNoReraise dflt1 dflt2 ([<InlineIfLambda>] f) =
    try
        f ()
    with
    | UnresolvedPathReferenceNoRange _ -> dflt1
    | _ -> dflt2

// Attach a range if this is a range dual exception.
let rec AttachRange m (exn: exn) =
    if equals m range0 then
        exn
    else
        match exn with
        // Strip TargetInvocationException wrappers
        | :? TargetInvocationException as e when isNotNull e.InnerException -> AttachRange m !!exn.InnerException
        | UnresolvedReferenceNoRange a -> UnresolvedReferenceError(a, m)
        | UnresolvedPathReferenceNoRange(a, p) -> UnresolvedPathReference(a, p, m)
        | :? NotSupportedException -> exn
        | :? SystemException -> InternalException(exn, exn.Message, m)
        | _ -> exn

type Exiter =
    abstract Exit: int -> 'T

let QuitProcessExiter =
    { new Exiter with
        member _.Exit n =
            try
                Environment.Exit n
            with _ ->
                ()

            failwith (FSComp.SR.elSysEnvExitDidntExit ())
    }

type StopProcessingExiter() =

    member val ExitCode = 0 with get, set

    interface Exiter with
        member exiter.Exit n =
            exiter.ExitCode <- n
            raise StopProcessing

/// Closed enumeration of build phases.
[<RequireQualifiedAccess>]
type BuildPhase =
    | DefaultPhase
    | Compile
    | Parameter
    | Parse
    | TypeCheck
    | CodeGen
    | Optimize
    | IlxGen
    | IlGen
    | Output
    | Interactive // An error seen during interactive execution

    override this.ToString() =
        match this with
        | DefaultPhase -> nameof DefaultPhase
        | Compile -> nameof Compile
        | Parameter -> nameof Parameter
        | Parse -> nameof Parse
        | TypeCheck -> nameof TypeCheck
        | CodeGen -> nameof CodeGen
        | Optimize -> nameof Optimize
        | IlxGen -> nameof IlxGen
        | IlGen -> nameof IlGen
        | Output -> nameof Output
        | Interactive -> nameof Interactive

/// Literal build phase subcategory strings.
module BuildPhaseSubcategory =
    [<Literal>]
    let DefaultPhase = ""

    [<Literal>]
    let Compile = "compile"

    [<Literal>]
    let Parameter = "parameter"

    [<Literal>]
    let Parse = "parse"

    [<Literal>]
    let TypeCheck = "typecheck"

    [<Literal>]
    let CodeGen = "codegen"

    [<Literal>]
    let Optimize = "optimize"

    [<Literal>]
    let IlxGen = "ilxgen"

    [<Literal>]
    let IlGen = "ilgen"

    [<Literal>]
    let Output = "output"

    [<Literal>]
    let Interactive = "interactive"

    [<Literal>]
    let Internal = "internal" // Compiler ICE

[<DebuggerDisplay("{DebugDisplay()}")>]
type PhasedDiagnostic =
    {
        Exception: exn
        Phase: BuildPhase
    }

    /// Construct a phased error
    static member Create(exn: exn, phase: BuildPhase) : PhasedDiagnostic = { Exception = exn; Phase = phase }

    member this.DebugDisplay() =
        sprintf "%s: %s" (this.Subcategory()) this.Exception.Message

    /// This is the textual subcategory to display in error and warning messages (shows only under --vserrors):
    ///
    ///     file1.fs(72): subcategory warning FS0072: This is a warning message
    ///
    member pe.Subcategory() =
        match pe.Phase with
        | BuildPhase.DefaultPhase -> BuildPhaseSubcategory.DefaultPhase
        | BuildPhase.Compile -> BuildPhaseSubcategory.Compile
        | BuildPhase.Parameter -> BuildPhaseSubcategory.Parameter
        | BuildPhase.Parse -> BuildPhaseSubcategory.Parse
        | BuildPhase.TypeCheck -> BuildPhaseSubcategory.TypeCheck
        | BuildPhase.CodeGen -> BuildPhaseSubcategory.CodeGen
        | BuildPhase.Optimize -> BuildPhaseSubcategory.Optimize
        | BuildPhase.IlxGen -> BuildPhaseSubcategory.IlxGen
        | BuildPhase.IlGen -> BuildPhaseSubcategory.IlGen
        | BuildPhase.Output -> BuildPhaseSubcategory.Output
        | BuildPhase.Interactive -> BuildPhaseSubcategory.Interactive

    /// Return true if the textual phase given is from the compile part of the build process.
    /// This set needs to be equal to the set of subcategories that the language service can produce.
    static member IsSubcategoryOfCompile(subcategory: string) =
        // This code logic is duplicated in DocumentTask.cs in the language service.
        match subcategory with
        | BuildPhaseSubcategory.Compile
        | BuildPhaseSubcategory.Parameter
        | BuildPhaseSubcategory.Parse
        | BuildPhaseSubcategory.TypeCheck -> true
        | BuildPhaseSubcategory.DefaultPhase
        | BuildPhaseSubcategory.CodeGen
        | BuildPhaseSubcategory.Optimize
        | BuildPhaseSubcategory.IlxGen
        | BuildPhaseSubcategory.IlGen
        | BuildPhaseSubcategory.Output
        | BuildPhaseSubcategory.Interactive -> false
        | BuildPhaseSubcategory.Internal
        // Getting here means the compiler has ICE-d. Let's not pile on by showing the unknownSubcategory assert below.
        // Just treat as an unknown-to-LanguageService error.
         -> false
        | unknownSubcategory ->
            Debug.Assert(false, sprintf "Subcategory '%s' could not be correlated with a build phase." unknownSubcategory)
            // Recovery is to treat this as a 'build' error. Downstream, the project system and language service will treat this as
            // if it came from the build and not the language service.
            false

    /// Return true if this phase is one that's known to be part of the 'compile'. This is the initial phase of the entire compilation that
    /// the language service knows about.
    member pe.IsPhaseInCompile() =
        match pe.Phase with
        | BuildPhase.Compile
        | BuildPhase.Parameter
        | BuildPhase.Parse
        | BuildPhase.TypeCheck -> true
        | _ -> false

[<AbstractClass>]
[<DebuggerDisplay("{DebugDisplay()}")>]
type DiagnosticsLogger(nameForDebugging: string) =
    abstract ErrorCount: int

    // The 'Impl' factoring enables a developer to place a breakpoint at the non-Impl
    // code just below and get a breakpoint for all error logger implementations.
    abstract DiagnosticSink: diagnostic: PhasedDiagnostic * severity: FSharpDiagnosticSeverity -> unit

    member x.CheckForErrors() = (x.ErrorCount > 0)

    abstract CheckForRealErrorsIgnoringWarnings: bool
    default x.CheckForRealErrorsIgnoringWarnings = x.CheckForErrors()

    member _.DebugDisplay() =
        sprintf "DiagnosticsLogger(%s)" nameForDebugging

let DiscardErrorsLogger =
    { new DiagnosticsLogger("DiscardErrorsLogger") with
        member _.DiagnosticSink(diagnostic, severity) = ()
        member _.ErrorCount = 0
    }

let AssertFalseDiagnosticsLogger =
    { new DiagnosticsLogger("AssertFalseDiagnosticsLogger") with
        // TODO: reenable these asserts in the compiler service
        member _.DiagnosticSink(diagnostic, severity) = (* assert false; *) ()
        member _.ErrorCount = (* assert false; *) 0
    }

type CapturingDiagnosticsLogger(nm, ?eagerFormat) =
    inherit DiagnosticsLogger(nm)
    let mutable errorCount = 0
    let diagnostics = ResizeArray()

    override _.DiagnosticSink(diagnostic, severity) =
        let diagnostic =
            match eagerFormat with
            | None -> diagnostic
            | Some f -> f diagnostic

        if severity = FSharpDiagnosticSeverity.Error then
            errorCount <- errorCount + 1

        diagnostics.Add(diagnostic, severity)

    override _.ErrorCount = errorCount

    member _.Diagnostics = diagnostics |> Seq.toList

    member _.CommitDelayedDiagnostics(diagnosticsLogger: DiagnosticsLogger) =
        // Eagerly grab all the errors and warnings from the mutable collection
        let errors = diagnostics.ToArray()
        errors |> Array.iter diagnosticsLogger.DiagnosticSink

let buildPhase = AsyncLocal<BuildPhase voption>()
let diagnosticsLogger = AsyncLocal<DiagnosticsLogger voption>()

/// Type holds thread-static globals for use by the compiler.
type internal DiagnosticsThreadStatics =

    static member BuildPhase
        with get () = buildPhase.Value |> ValueOption.defaultValue BuildPhase.DefaultPhase
        and set v = buildPhase.Value <- ValueSome v

    static member DiagnosticsLogger
        with get () = diagnosticsLogger.Value |> ValueOption.defaultValue AssertFalseDiagnosticsLogger
        and set v = diagnosticsLogger.Value <- ValueSome v

[<AutoOpen>]
module DiagnosticsLoggerExtensions =

    // Dev15.0 shipped with a bug in diasymreader in the portable pdb symbol reader which causes an AV
    // This uses a simple heuristic to detect it (the vsversion is < 16.0)
    let tryAndDetectDev15 =
        let vsVersion = Environment.GetEnvironmentVariable("VisualStudioVersion")

        match Double.TryParse vsVersion with
        | true, v -> v < 16.0
        | _ -> false

    /// Instruct the exception not to reset itself when thrown again.
    let PreserveStackTrace exn =
        try
            if not tryAndDetectDev15 then
                let preserveStackTrace =
                    !!typeof<Exception>.GetMethod("InternalPreserveStackTrace", BindingFlags.Instance ||| BindingFlags.NonPublic)

                preserveStackTrace.Invoke(exn, null) |> ignore
        with _ ->
            // This is probably only the mono case.
            Debug.Assert(false, "Could not preserve stack trace for watson exception.")
            ()

    type DiagnosticsLogger with

        member x.EmitDiagnostic(exn, severity) =

            match exn with
            | InternalError(s, _)
            | InternalException(_, s, _)
            | Failure s as exn -> Debug.Assert(false, sprintf "Unexpected exception raised in compiler: %s\n%s" s (exn.ToString()))
            | _ -> ()

            match exn with
            | StopProcessing
            | ReportedError _ ->
                PreserveStackTrace exn
                raise exn
            | _ -> x.DiagnosticSink(PhasedDiagnostic.Create(exn, DiagnosticsThreadStatics.BuildPhase), severity)

        member x.ErrorR exn =
            x.EmitDiagnostic(exn, FSharpDiagnosticSeverity.Error)

        member x.Warning exn =
            x.EmitDiagnostic(exn, FSharpDiagnosticSeverity.Warning)

        member x.InformationalWarning exn =
            x.EmitDiagnostic(exn, FSharpDiagnosticSeverity.Info)

        member x.Error exn =
            x.ErrorR exn
            raise (ReportedError(Some exn))

        member x.SimulateError diagnostic =
            x.DiagnosticSink(diagnostic, FSharpDiagnosticSeverity.Error)
            raise (ReportedError(Some diagnostic.Exception))

        member x.ErrorRecovery (exn: exn) (m: range) =
            // Never throws ReportedError.
            // Throws StopProcessing and exceptions raised by the DiagnosticSink(exn) handler.
            match exn with
            // Don't send ThreadAbortException down the error channel
            | :? System.Threading.ThreadAbortException
            | WrappedError(:? System.Threading.ThreadAbortException, _) -> ()
            | ReportedError _
            | WrappedError(ReportedError _, _) -> ()
            | StopProcessing
            | WrappedError(StopProcessing, _) ->
                PreserveStackTrace exn
                raise exn
            | _ ->
                try
                    x.ErrorR(AttachRange m exn) // may raise exceptions, e.g. an fsi error sink raises StopProcessing.
                with
                | ReportedError _
                | WrappedError(ReportedError _, _) -> ()

        member x.StopProcessingRecovery (exn: exn) (m: range) =
            // Do standard error recovery.
            // Additionally ignore/catch StopProcessing. [This is the only catch handler for StopProcessing].
            // Additionally ignore/catch ReportedError.
            // Can throw other exceptions raised by the DiagnosticSink(exn) handler.
            match exn with
            | StopProcessing
            | WrappedError(StopProcessing, _) -> () // suppress, so skip error recovery.
            | _ ->
                try
                    x.ErrorRecovery exn m
                with
                | StopProcessing
                | WrappedError(StopProcessing, _) -> () // catch, e.g. raised by DiagnosticSink.
                | ReportedError _
                | WrappedError(ReportedError _, _) -> () // catch, but not expected unless ErrorRecovery is changed.

        member x.ErrorRecoveryNoRange(exn: exn) = x.ErrorRecovery exn range0

/// NOTE: The change will be undone when the returned "unwind" object disposes
let UseBuildPhase (phase: BuildPhase) =
    let oldBuildPhase = buildPhase.Value
    DiagnosticsThreadStatics.BuildPhase <- phase

    { new IDisposable with
        member x.Dispose() = buildPhase.Value <- oldBuildPhase
    }

/// NOTE: The change will be undone when the returned "unwind" object disposes
let UseTransformedDiagnosticsLogger (transformer: DiagnosticsLogger -> DiagnosticsLogger) =
    let oldLogger = DiagnosticsThreadStatics.DiagnosticsLogger
    DiagnosticsThreadStatics.DiagnosticsLogger <- transformer oldLogger

    { new IDisposable with
        member _.Dispose() =
            DiagnosticsThreadStatics.DiagnosticsLogger <- oldLogger
    }

let UseDiagnosticsLogger newLogger =
    UseTransformedDiagnosticsLogger(fun _ -> newLogger)

let SetThreadBuildPhaseNoUnwind (phase: BuildPhase) =
    DiagnosticsThreadStatics.BuildPhase <- phase

let SetThreadDiagnosticsLoggerNoUnwind diagnosticsLogger =
    DiagnosticsThreadStatics.DiagnosticsLogger <- diagnosticsLogger

/// This represents the thread-local state established as each task function runs as part of the build.
///
/// Use to reset error and warning handlers.
type CompilationGlobalsScope(diagnosticsLogger: DiagnosticsLogger, buildPhase: BuildPhase) =
    let unwindEL = UseDiagnosticsLogger diagnosticsLogger
    let unwindBP = UseBuildPhase buildPhase

    new() = new CompilationGlobalsScope(DiagnosticsThreadStatics.DiagnosticsLogger, DiagnosticsThreadStatics.BuildPhase)

    member _.DiagnosticsLogger = diagnosticsLogger
    member _.BuildPhase = buildPhase

    // Return the disposable object that cleans up
    interface IDisposable with
        member _.Dispose() =
            unwindBP.Dispose()
            unwindEL.Dispose()

// Global functions are still used by parser and TAST ops.

/// Raises an exception with error recovery and returns unit.
let errorR exn =
    DiagnosticsThreadStatics.DiagnosticsLogger.ErrorR exn

/// Raises a warning with error recovery and returns unit.
let warning exn =
    DiagnosticsThreadStatics.DiagnosticsLogger.Warning exn

/// Raises a warning with error recovery and returns unit.
let informationalWarning exn =
    DiagnosticsThreadStatics.DiagnosticsLogger.InformationalWarning exn

/// Raises a special exception and returns 'T - can be caught later at an errorRecovery point.
let error exn =
    DiagnosticsThreadStatics.DiagnosticsLogger.Error exn

/// Simulates an error. For test purposes only.
let simulateError (diagnostic: PhasedDiagnostic) =
    DiagnosticsThreadStatics.DiagnosticsLogger.SimulateError diagnostic

let diagnosticSink (diagnostic, severity) =
    DiagnosticsThreadStatics.DiagnosticsLogger.DiagnosticSink(diagnostic, severity)

let errorSink diagnostic =
    diagnosticSink (diagnostic, FSharpDiagnosticSeverity.Error)

let warnSink diagnostic =
    diagnosticSink (diagnostic, FSharpDiagnosticSeverity.Warning)

let errorRecovery exn m =
    DiagnosticsThreadStatics.DiagnosticsLogger.ErrorRecovery exn m

let stopProcessingRecovery exn m =
    DiagnosticsThreadStatics.DiagnosticsLogger.StopProcessingRecovery exn m

let errorRecoveryNoRange exn =
    DiagnosticsThreadStatics.DiagnosticsLogger.ErrorRecoveryNoRange exn

let deprecatedWithError s m = errorR (Deprecated(s, m))

let libraryOnlyError m = errorR (LibraryUseOnly m)

let libraryOnlyWarning m = warning (LibraryUseOnly m)

let deprecatedOperator m =
    deprecatedWithError (FSComp.SR.elDeprecatedOperator ()) m

let mlCompatWarning s m =
    warning (UserCompilerMessage(FSComp.SR.mlCompatMessage s, 62, m))

let mlCompatError s m =
    errorR (UserCompilerMessage(FSComp.SR.mlCompatError s, 62, m))

[<DebuggerStepThrough>]
let suppressErrorReporting f =
    let diagnosticsLogger = DiagnosticsThreadStatics.DiagnosticsLogger

    try
        let diagnosticsLogger =
            { new DiagnosticsLogger("suppressErrorReporting") with
                member _.DiagnosticSink(_phasedError, _isError) = ()
                member _.ErrorCount = 0
            }

        SetThreadDiagnosticsLoggerNoUnwind diagnosticsLogger
        f ()
    finally
        SetThreadDiagnosticsLoggerNoUnwind diagnosticsLogger

[<DebuggerStepThrough>]
let conditionallySuppressErrorReporting cond f =
    if cond then suppressErrorReporting f else f ()

//------------------------------------------------------------------------
// Errors as data: Sometimes we have to reify errors as data, e.g. if backtracking

/// The result type of a computational modality to collect warnings and possibly fail
[<NoEquality; NoComparison>]
type OperationResult<'T> =
    | OkResult of warnings: exn list * result: 'T
    | ErrorResult of warnings: exn list * error: exn

type ImperativeOperationResult = OperationResult<unit>

let ReportWarnings warns =
    match warns with
    | [] -> () // shortcut in common case
    | _ -> List.iter warning warns

let CommitOperationResult res =
    match res with
    | OkResult(warns, res) ->
        ReportWarnings warns
        res
    | ErrorResult(warns, err) ->
        ReportWarnings warns
        error err

let RaiseOperationResult res : unit = CommitOperationResult res

let inline ErrorD err = ErrorResult([], err)

let inline WarnD err = OkResult([ err ], ())

let CompleteD = OkResult([], ())

let inline ResultD x = OkResult([], x)

let CheckNoErrorsAndGetWarnings res =
    match res with
    | OkResult(warns, res2) -> Some(warns, res2)
    | ErrorResult _ -> None

[<DebuggerHidden; DebuggerStepThrough>]
let inline bind f res =
    match res with
    | OkResult([], res) -> (* tailcall *) f res
    | OkResult(warns, res) ->
        match f res with
        | OkResult(warns2, res2) -> OkResult(warns @ warns2, res2)
        | ErrorResult(warns2, err) -> ErrorResult(warns @ warns2, err)
    | ErrorResult(warns, err) -> ErrorResult(warns, err)

/// Stop on first error. Accumulate warnings and continue.
[<DebuggerHidden; DebuggerStepThrough>]
let rec IterateD f xs =
    match xs with
    | [] -> CompleteD
    | h :: t -> f h |> bind (fun () -> IterateD f t)

[<DebuggerHidden; DebuggerStepThrough>]
let rec WhileD gd body =
    if gd () then
        body () |> bind (fun () -> WhileD gd body)
    else
        CompleteD

[<DebuggerHidden; DebuggerStepThrough>]
let rec MapD_loop f acc xs =
    match xs with
    | [] -> ResultD(List.rev acc)
    | h :: t -> f h |> bind (fun x -> MapD_loop f (x :: acc) t)

[<DebuggerHidden; DebuggerStepThrough>]
let MapD f xs = MapD_loop f [] xs

type TrackErrorsBuilder() =
    member inline x.Bind(res, k) = bind k res
    member inline x.Return res = ResultD res
    member inline x.ReturnFrom res = res
    member inline x.For(seq, k) = IterateD k seq
    member inline x.Combine(expr1, expr2) = bind expr2 expr1
    member inline x.While(gd, k) = WhileD gd k
    member inline x.Zero() = CompleteD
    member inline x.Delay(fn: unit -> _) = fn
    member inline x.Run fn = fn ()

let trackErrors = TrackErrorsBuilder()

/// Stop on first error. Accumulate warnings and continue.
[<DebuggerHidden; DebuggerStepThrough>]
let OptionD f xs =
    match xs with
    | None -> CompleteD
    | Some h -> f h

/// Stop on first error. Report index
[<DebuggerHidden; DebuggerStepThrough>]
let IterateIdxD f xs =
    let rec loop xs i =
        match xs with
        | [] -> CompleteD
        | h :: t -> f i h |> bind (fun () -> loop t (i + 1))

    loop xs 0

/// Stop on first error. Accumulate warnings and continue.
[<DebuggerHidden; DebuggerStepThrough>]
let rec Iterate2D f xs ys =
    match xs, ys with
    | [], [] -> CompleteD
    | h1 :: t1, h2 :: t2 -> f h1 h2 |> bind (fun () -> Iterate2D f t1 t2)
    | _ -> failwith "Iterate2D"

/// Keep the warnings, propagate the error to the exception continuation.
[<DebuggerHidden; DebuggerStepThrough>]
let TryD f g =
    match f () with
    | ErrorResult(warns, err) ->
        trackErrors {
            do! OkResult(warns, ())
            return! g err
        }
    | res -> res

[<DebuggerHidden; DebuggerStepThrough>]
let rec RepeatWhileD nDeep body =
    body nDeep
    |> bind (fun x -> if x then RepeatWhileD (nDeep + 1) body else CompleteD)

[<DebuggerHidden; DebuggerStepThrough>]
let inline AtLeastOneD f l =
    MapD f l |> bind (fun res -> ResultD(List.exists id res))

[<DebuggerHidden; DebuggerStepThrough>]
let inline AtLeastOne2D f xs ys =
    List.zip xs ys |> AtLeastOneD(fun (x, y) -> f x y)

[<DebuggerHidden; DebuggerStepThrough>]
let inline MapReduceD mapper zero reducer l =
    MapD mapper l
    |> bind (fun res ->
        ResultD(
            match res with
            | [] -> zero
            | _ -> List.reduce reducer res
        ))

[<DebuggerHidden; DebuggerStepThrough>]
let inline MapReduce2D mapper zero reducer xs ys =
    List.zip xs ys |> MapReduceD (fun (x, y) -> mapper x y) zero reducer

[<RequireQualifiedAccess>]
module OperationResult =
    let inline ignore (res: OperationResult<'a>) =
        match res with
        | OkResult(warnings, _) -> OkResult(warnings, ())
        | ErrorResult(warnings, err) -> ErrorResult(warnings, err)

// Code below is for --flaterrors flag that is only used by the IDE
let stringThatIsAProxyForANewlineInFlatErrors = String [| char 29 |]

let NewlineifyErrorString (message: string) =
    message.Replace(stringThatIsAProxyForANewlineInFlatErrors, Environment.NewLine)

/// fixes given string by replacing all control chars with spaces.
/// NOTE: newlines are recognized and replaced with stringThatIsAProxyForANewlineInFlatErrors (ASCII 29, the 'group separator'),
/// which is decoded by the IDE with 'NewlineifyErrorString' back into newlines, so that multi-line errors can be displayed in QuickInfo
let NormalizeErrorString (text: string) =
    let text = text.Trim()

    let buf = System.Text.StringBuilder()
    let mutable i = 0

    while i < text.Length do
        let delta =
            match text[i] with
            | '\r' when i + 1 < text.Length && text[i + 1] = '\n' ->
                // handle \r\n sequence - replace it with one single space
                buf.Append stringThatIsAProxyForANewlineInFlatErrors |> ignore
                2
            | '\n'
            | '\r' ->
                buf.Append stringThatIsAProxyForANewlineInFlatErrors |> ignore
                1
            | c ->
                // handle remaining chars: control - replace with space, others - keep unchanged
                let c = if Char.IsControl c then ' ' else c
                buf.Append c |> ignore
                1

        i <- i + delta

    buf.ToString()

/// Indicates whether a language feature check should be skipped. Typically used in recursive functions
/// where we don't want repeated recursive calls to raise the same diagnostic multiple times.
[<RequireQualifiedAccess>]
type internal SuppressLanguageFeatureCheck =
    | Yes
    | No

let internal languageFeatureError (langVersion: LanguageVersion) (langFeature: LanguageFeature) (m: range) =
    let featureStr = LanguageVersion.GetFeatureString langFeature
    let currentVersionStr = langVersion.SpecifiedVersionString
    let suggestedVersionStr = LanguageVersion.GetFeatureVersionString langFeature
    Error(FSComp.SR.chkFeatureNotLanguageSupported (featureStr, currentVersionStr, suggestedVersionStr), m)

let internal tryLanguageFeatureErrorOption (langVersion: LanguageVersion) (langFeature: LanguageFeature) (m: range) =
    if not (langVersion.SupportsFeature langFeature) then
        Some(languageFeatureError langVersion langFeature m)
    else
        None

let internal checkLanguageFeatureError langVersion langFeature m =
    match tryLanguageFeatureErrorOption langVersion langFeature m with
    | Some e -> error e
    | None -> ()

let internal tryCheckLanguageFeatureAndRecover langVersion langFeature m =
    match tryLanguageFeatureErrorOption langVersion langFeature m with
    | Some e ->
        errorR e
        false
    | None -> true

let internal checkLanguageFeatureAndRecover langVersion langFeature m =
    tryCheckLanguageFeatureAndRecover langVersion langFeature m |> ignore

let internal languageFeatureNotSupportedInLibraryError (langFeature: LanguageFeature) (m: range) =
    let featureStr = LanguageVersion.GetFeatureString langFeature
    let suggestedVersionStr = LanguageVersion.GetFeatureVersionString langFeature
    error (Error(FSComp.SR.chkFeatureNotSupportedInLibrary (featureStr, suggestedVersionStr), m))

/// Guard against depth of expression nesting, by moving to new stack when a maximum depth is reached
type StackGuard(maxDepth: int, name: string) =

    let mutable depth = 1

    [<DebuggerHidden; DebuggerStepThrough>]
    member _.Guard
        (
            f,
            [<CallerMemberName; Optional; DefaultParameterValue("")>] memberName: string,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] path: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) =

        Activity.addEventWithTags
            "DiagnosticsLogger.StackGuard.Guard"
            (seq {
                Activity.Tags.stackGuardName, box name
                Activity.Tags.stackGuardCurrentDepth, depth
                Activity.Tags.stackGuardMaxDepth, maxDepth
                Activity.Tags.callerMemberName, memberName
                Activity.Tags.callerFilePath, path
                Activity.Tags.callerLineNumber, line
            })

        depth <- depth + 1

        try
            if depth % maxDepth = 0 then

                async {
                    do! Async.SwitchToNewThread()
                    Thread.CurrentThread.Name <- $"F# Extra Compilation Thread for {name} (depth {depth})"
                    return f ()
                }
                |> Async.RunImmediate
            else
                f ()
        finally
            depth <- depth - 1

    [<DebuggerHidden; DebuggerStepThrough>]
    member x.GuardCancellable(original: Cancellable<'T>) =
        Cancellable(fun ct -> x.Guard(fun () -> Cancellable.run ct original))

    static member val DefaultDepth =
#if DEBUG
        GetEnvInteger "FSHARP_DefaultStackGuardDepth" 50
#else
        GetEnvInteger "FSHARP_DefaultStackGuardDepth" 100
#endif

    static member GetDepthOption(name: string) =
        GetEnvInteger ("FSHARP_" + name + "StackGuardDepth") StackGuard.DefaultDepth

// UseMultipleDiagnosticLoggers in ParseAndCheckProject.fs provides similar functionality.
// We should probably adapt and reuse that code.
module MultipleDiagnosticsLoggers =
    let Parallel computations =
        let computationsWithLoggers, diagnosticsReady =
            [
                for i, computation in computations |> Seq.indexed do
                    let diagnosticsReady = TaskCompletionSource<_>()

                    let logger = CapturingDiagnosticsLogger($"CaptureDiagnosticsConcurrently {i}")

                    // Inject capturing logger into the computation. Signal the TaskCompletionSource when done.
                    let computationsWithLoggers =
                        async {
                            SetThreadDiagnosticsLoggerNoUnwind logger

                            try
                                return! computation
                            finally
                                diagnosticsReady.SetResult logger
                        }

                    computationsWithLoggers, diagnosticsReady
            ]
            |> List.unzip

        // Commit diagnostics from computations as soon as it is possible, preserving the order.
        let replayDiagnostics =
            backgroundTask {
                let target = DiagnosticsThreadStatics.DiagnosticsLogger

                for tcs in diagnosticsReady do
                    let! finishedLogger = tcs.Task
                    finishedLogger.CommitDelayedDiagnostics target
            }

        async {
            try
                // We want to restore the current diagnostics context when finished.
                use _ = new CompilationGlobalsScope()
                let! results = Async.Parallel computationsWithLoggers
                do! replayDiagnostics |> Async.AwaitTask
                return results
            finally
                // When any of the computation throws, Async.Parallel may not start some remaining computations at all.
                // We set dummy results for them to allow the task to finish and to not lose any already emitted diagnostics.
                if not replayDiagnostics.IsCompleted then
                    let emptyLogger = CapturingDiagnosticsLogger("empty")

                    for tcs in diagnosticsReady do
                        tcs.TrySetResult(emptyLogger) |> ignore

                    replayDiagnostics.Wait()
        }

    let Sequential computations =
        async {
            let results = ResizeArray()

            for computation in computations do
                let! result = computation
                results.Add result

            return results.ToArray()
        }
