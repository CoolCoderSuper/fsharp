// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Contains logic to coordinate the parsing and checking of one or a group of files
module internal FSharp.Compiler.ParseAndCheckInputs

open System.IO
open Internal.Utilities.Library
open FSharp.Compiler.CheckBasics
open FSharp.Compiler.CheckDeclarations
open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.CompilerImports
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.DependencyManager
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Features
open FSharp.Compiler.GraphChecking
open FSharp.Compiler.NameResolution
open FSharp.Compiler.Syntax
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.TypedTree
open FSharp.Compiler.UnicodeLexing

/// Auxiliary type for re-using signature information in TcEnvFromImpls.
///
/// TcState has two typing environments: TcEnvFromSignatures && TcEnvFromImpls
/// When type checking a file, depending on the type (implementation or signature), it will use one of these typing environments (TcEnv).
/// Checking a file will populate the respective TcEnv.
///
/// When a file has a dependencies, the information of the signature file in case a pair (implementation file backed by a signature) will suffice to type-check that file.
/// Example: if `B.fs` has a dependency on `A`, the information of `A.fsi` is enough for `B.fs` to type-check, on condition that information is available in the TcEnvFromImpls.
/// We introduce a special ArtificialImplFile node in the graph to satisfy this. `B.fs -> [ A.fsi ]` becomes `B.fs -> [ ArtificialImplFile A ].
/// The `ArtificialImplFile A` node will duplicate the signature information which A.fsi provided earlier.
/// Processing a `ArtificialImplFile` node will add the information from the TcEnvFromSignatures to the TcEnvFromImpls.
/// This means `A` will be known in both TcEnvs and therefor `B.fs` can be type-checked.
/// By doing this, we can speed up the graph processing as type checking a signature file is less expensive than its implementation counterpart.
///
/// When we need to actually type-check an implementation file backed by a signature, we cannot have the duplicate information of the signature file present in TcEnvFromImpls.
/// Example `A.fs -> [ A.fsi ]`. An implementation file always depends on its signature.
/// Type-checking `A.fs` will add the actual information to TcEnvFromImpls and we do not depend on the `ArtificialImplFile A` for `A.fs`.
///
/// In order to deal correctly with the `ArtificialImplFile` logic, we need to transform the resolved graph to contain the additional pair nodes.
/// After we have type-checked the graph, we exclude the ArtificialImplFile nodes as they are not actual physical files in our project.
[<RequireQualifiedAccess>]
type NodeToTypeCheck =
    /// A real physical file in the current project.
    /// This can be either an implementation or a signature file.
    | PhysicalFile of fileIndex: FileIndex
    /// An artificial node that will add the earlier processed signature information to the TcEnvFromImpls.
    /// Dependents on this type of node will perceive that a file is known in both TcEnvFromSignatures and TcEnvFromImpls.
    /// Even though the actual implementation file was not type-checked.
    | ArtificialImplFile of signatureFileIndex: FileIndex

val IsScript: string -> bool

val ComputeQualifiedNameOfFileFromUniquePath: range * string list -> QualifiedNameOfFile

val PrependPathToInput: Ident list -> ParsedInput -> ParsedInput

/// State used to de-deduplicate module names along a list of file names
type ModuleNamesDict = Map<string, Map<string, QualifiedNameOfFile>>

/// Checks if a ParsedInput is using a module name that was already given and deduplicates the name if needed.
val DeduplicateParsedInputModuleName: ModuleNamesDict -> ParsedInput -> ParsedInput * ModuleNamesDict

/// Parse a single input (A signature file or implementation file)
val ParseInput:
    lexer: (Lexbuf -> Parser.token) *
    diagnosticOptions: FSharpDiagnosticOptions *
    diagnosticsLogger: DiagnosticsLogger *
    lexbuf: Lexbuf *
    defaultNamespace: string option *
    fileName: string *
    isLastCompiland: (bool * bool) *
    identCapture: bool *
    userOpName: string option ->
        ParsedInput

/// A general routine to process hash directives
val ProcessMetaCommandsFromInput:
    ('T -> range * string * Directive -> 'T) * ('T -> range * string -> unit) ->
        TcConfigBuilder * ParsedInput * string * 'T ->
            'T

/// Process all the #r, #I etc. in an input.
val ApplyMetaCommandsFromInputToTcConfig: TcConfig * ParsedInput * string * DependencyProvider -> TcConfig

/// Parse one input stream
val ParseOneInputStream:
    tcConfig: TcConfig *
    lexResourceManager: Lexhelp.LexResourceManager *
    fileName: string *
    isLastCompiland: (bool * bool) *
    diagnosticsLogger: DiagnosticsLogger *
    retryLocked: bool *
    stream: Stream ->
        ParsedInput

/// Parse one input source text
val ParseOneInputSourceText:
    tcConfig: TcConfig *
    lexResourceManager: Lexhelp.LexResourceManager *
    fileName: string *
    isLastCompiland: (bool * bool) *
    diagnosticsLogger: DiagnosticsLogger *
    sourceText: ISourceText ->
        ParsedInput

/// Parse one input file
val ParseOneInputFile:
    tcConfig: TcConfig *
    lexResourceManager: Lexhelp.LexResourceManager *
    fileName: string *
    isLastCompiland: (bool * bool) *
    diagnosticsLogger: DiagnosticsLogger *
    retryLocked: bool ->
        ParsedInput

val ParseOneInputLexbuf:
    tcConfig: TcConfig *
    lexResourceManager: Lexhelp.LexResourceManager *
    lexbuf: Lexbuf *
    fileName: string *
    isLastCompiland: (bool * bool) *
    diagnosticsLogger: DiagnosticsLogger ->
        ParsedInput

val EmptyParsedInput: fileName: string * isLastCompiland: (bool * bool) -> ParsedInput

/// Parse multiple input files from disk
val ParseInputFiles:
    tcConfig: TcConfig *
    lexResourceManager: Lexhelp.LexResourceManager *
    sourceFiles: string list *
    diagnosticsLogger: DiagnosticsLogger *
    retryLocked: bool ->
        (ParsedInput * string) list

/// Get the initial type checking environment including the loading of mscorlib/System.Core, FSharp.Core
/// applying the InternalsVisibleTo in referenced assemblies and opening 'Checked' if requested.
val GetInitialTcEnv: assemblyName: string * range * TcConfig * TcImports * TcGlobals -> TcEnv * OpenDeclaration list

/// Represents the incremental type checking state for a set of inputs
[<Sealed>]
type TcState =
    /// The CcuThunk for the current assembly being checked
    member Ccu: CcuThunk

    /// Get the typing environment implied by the set of signature files and/or inferred signatures of implementation files checked so far
    member TcEnvFromSignatures: TcEnv

    /// Get the typing environment implied by the set of implementation files checked so far
    member TcEnvFromImpls: TcEnv

    /// The inferred contents of the assembly, containing the signatures of all files.
    // a.fsi + b.fsi + c.fsi (after checking implementation file for c.fs)
    member CcuSig: ModuleOrNamespaceType

    member NextStateAfterIncrementalFragment: TcEnv -> TcState

    member CreatesGeneratedProvidedTypes: bool

type PartialResult = TcEnv * TopAttribs * CheckedImplFile option * ModuleOrNamespaceType

/// Get the initial type checking state for a set of inputs
val GetInitialTcState: range * string * TcConfig * TcGlobals * TcImports * TcEnv * OpenDeclaration list -> TcState

/// Returns partial type check result for skipped implementation files.
val SkippedImplFilePlaceholder:
    tcConfig: TcConfig * tcImports: TcImports * tcGlobals: TcGlobals * tcState: TcState * input: ParsedInput ->
        ((TcEnv * TopAttribs * CheckedImplFile option * ModuleOrNamespaceType) * TcState) option

/// Check one input, returned as an Eventually computation
val CheckOneInput:
    checkForErrors: (unit -> bool) *
    tcConfig: TcConfig *
    tcImports: TcImports *
    tcGlobals: TcGlobals *
    prefixPathOpt: LongIdent option *
    tcSink: NameResolution.TcResultsSink *
    tcState: TcState *
    input: ParsedInput ->
        Cancellable<(TcEnv * TopAttribs * CheckedImplFile option * ModuleOrNamespaceType) * TcState>

val CheckOneInputWithCallback:
    node: NodeToTypeCheck ->
    checkForErrors: (unit -> bool) *
    tcConfig: TcConfig *
    tcImports: TcImports *
    tcGlobals: TcGlobals *
    prefixPathOpt: LongIdent option *
    tcSink: TcResultsSink *
    tcState: TcState *
    input: ParsedInput *
    _skipImplIfSigExists: bool ->
        Cancellable<Finisher<NodeToTypeCheck, TcState, PartialResult>>

val AddCheckResultsToTcState:
    tcGlobals: TcGlobals *
    amap: Import.ImportMap *
    hadSig: bool *
    prefixPathOpt: LongIdent option *
    tcSink: TcResultsSink *
    tcImplEnv: TcEnv *
    qualNameOfFile: QualifiedNameOfFile *
    implFileSigType: ModuleOrNamespaceType ->
        tcState: TcState ->
            ModuleOrNamespaceType * TcState

val AddSignatureResultToTcImplEnv:
    tcImports: TcImports *
    tcGlobals: TcGlobals *
    prefixPathOpt: LongIdent option *
    tcSink: TcResultsSink *
    tcState: TcState *
    input: ParsedInput ->
        (TcState -> PartialResult * TcState)

val TransformDependencyGraph: graph: Graph<FileIndex> * filePairs: FilePairMap -> Graph<NodeToTypeCheck>

/// Finish the checking of multiple inputs
val CheckMultipleInputsFinish:
    (TcEnv * TopAttribs * 'T option * 'U) list * TcState -> (TcEnv * TopAttribs * 'T list * 'U list) * TcState

/// Finish the checking of a closed set of inputs
val CheckClosedInputSetFinish: CheckedImplFile list * TcState -> TcState * CheckedImplFile list * ModuleOrNamespace

/// Check a closed set of inputs
val CheckClosedInputSet:
    ctok: CompilationThreadToken *
    checkForErrors: (unit -> bool) *
    tcConfig: TcConfig *
    tcImports: TcImports *
    tcGlobals: TcGlobals *
    prefixPathOpt: LongIdent option *
    tcState: TcState *
    eagerFormat: (PhasedDiagnostic -> PhasedDiagnostic) *
    inputs: ParsedInput list ->
        TcState * TopAttribs * CheckedImplFile list * TcEnv

/// Check a single input and finish the checking
val CheckOneInputAndFinish:
    checkForErrors: (unit -> bool) *
    tcConfig: TcConfig *
    tcImports: TcImports *
    tcGlobals: TcGlobals *
    prefixPathOpt: LongIdent option *
    tcSink: NameResolution.TcResultsSink *
    tcState: TcState *
    input: ParsedInput ->
        Cancellable<(TcEnv * TopAttribs * CheckedImplFile list * ModuleOrNamespaceType list) * TcState>
