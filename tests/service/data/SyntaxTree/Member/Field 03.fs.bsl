ImplFile
  (ParsedImplFileInput
     ("/root/Member/Field 03.fs", false, QualifiedNameOfFile Module, [],
      [SynModuleOrNamespace
         ([Module], false, NamedModule,
          [Types
             ([SynTypeDefn
                 (SynComponentInfo
                    ([], None, [], [T],
                     PreXmlDoc ((3,0), FSharp.Compiler.Xml.XmlDocCollector),
                     false, None, (3,5--3,6)),
                  ObjectModel
                    (Unspecified,
                     [ValField
                        (SynField
                           ([], false, Some F1, FromParseError (4,10--4,10),
                            false,
                            PreXmlDoc ((4,4), FSharp.Compiler.Xml.XmlDocCollector),
                            None, (4,4--4,10),
                            { LeadingKeyword = Some (Val (4,4--4,7))
                              MutableKeyword = None }), (4,4--4,10));
                      ValField
                        (SynField
                           ([], false, Some F2,
                            LongIdent (SynLongIdent ([int], [], [None])), false,
                            PreXmlDoc ((5,4), FSharp.Compiler.Xml.XmlDocCollector),
                            None, (5,4--5,15),
                            { LeadingKeyword = Some (Val (5,4--5,7))
                              MutableKeyword = None }), (5,4--5,15))],
                     (4,4--5,15)), [], None, (3,5--5,15),
                  { LeadingKeyword = Type (3,0--3,4)
                    EqualsRange = Some (3,7--3,8)
                    WithKeyword = None })], (3,0--5,15));
           Expr (Const (Unit, (7,0--7,2)), (7,0--7,2))],
          PreXmlDoc ((1,0), FSharp.Compiler.Xml.XmlDocCollector), [], None,
          (1,0--7,2), { LeadingKeyword = Module (1,0--1,6) })], (true, true),
      { ConditionalDirectives = []
        WarnDirectives = []
        CodeComments = [] }, set []))

(4,11)-(5,4) parse error Incomplete structured construct at or before this point in type definition. Expected ':' or other token.
