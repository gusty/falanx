namespace Falanx.Proto.Generator

type TypeContainer = class end
    
module Proto =
    open Falanx.Machinery
    open Falanx.Proto.Generator.TypeGeneration
    open System
    open Microsoft.FSharp.Compiler.Ast
    open Microsoft.FSharp.Compiler.Range
    open Froto.Parser.ClassModel
    open FsAst
    open ProviderImplementation.ProvidedTypes
    open System.Text.RegularExpressions
    open System.IO
    
    let createProvidedTypes protoDef defaultnamespace (codecs: Codec Set) =
        let protoFile = ProtoFile.fromString protoDef
                                    
        let scope = protoFile.Packages |> Seq.tryHead |> Option.defaultValue defaultnamespace
    
        let provider = 
            ProvidedTypeDefinition(
                Reflection.Assembly.GetCallingAssembly(),
                scope,
                typeof<TypeContainer>.Name,
                Some typeof<obj>, 
                hideObjectMethods = true, 
                isErased = false)
                
        let container = provider 
    
        let typelookup = TypeResolver.discoverTypes scope protoFile
        
        protoFile.Enums
        |> Seq.map (TypeGeneration.createEnum scope typelookup)
        |> Seq.iter container.AddMember
        
        let generatedTypes =
            protoFile.Messages
            |> Seq.map (TypeGeneration.createType container scope typelookup codecs)
            |> Seq.iter container.AddMember
        provider

    /// Replace the autogenerated binding names like `_bind_e2b0f798140b4eebb9f5435d5d776098` with
    /// a stable `_bind_n` where `n` is an incremental int
    let private normalizeBindingNames (s: string) =
        let bindingNamePrefix = "_bind_"

        let bindingNames =
            let pattern = sprintf "%s[a-z0-9]+" (Regex.Escape(bindingNamePrefix))
            let matches = Regex.Matches(s, pattern, RegexOptions.IgnoreCase)

            [ for m in matches do
                if m.Success then
                    yield m.Value ]
            |> List.distinct
        
        let bindingSubst =
            bindingNames
            |> List.mapi (fun index name -> (name, sprintf "%s%i" bindingNamePrefix index))

        (s, bindingSubst)
        ||> List.fold (fun text (oldName, newName) -> text.Replace(oldName, newName) )


    let createFSharpDefinitions(protoDef: string, outputFile, defaultnamespace, codecs: Codec Set) =    
        let config = TypeProviderConfig.makeConfig "resoultionfolder" "runtimeAssembly.dll" []
        use typeProviderForNamespaces = new TypeProviderForNamespaces(config)
        let providedTypeRoot = createProvidedTypes protoDef defaultnamespace codecs
                
        let openSystem = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "System")
        let openFrotoSerialization = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "Froto.Serialization")
        let openSystemCollectionsGeneric = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "System.Collections.Generic")
        let openBinaryCodec = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "Falanx.Proto.Codec.Binary")
        let openBinaryCodecPrimitive = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "Falanx.Proto.Codec.Binary.Primitives")
        let openJsonLinq = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "Newtonsoft.Json.Linq")
        let openFleeceNewtonsoft = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "Fleece.Newtonsoft")
        let openJsonCodec = SynModuleDecl.CreateOpen (LongIdentWithDots.CreateString "Falanx.Proto.Codec.Json" )
        
        let knownNamespaces =
            [ yield providedTypeRoot.Namespace
              yield "System"
              yield "System.Collections.Generic"
              if codecs.Contains Binary then
                  yield "Froto.Serialization"
                  yield "Falanx.Proto.Codec.Binary"
                  yield "Falanx.Proto.Codec.Binary.Primitives"
                  
              if codecs.Contains Json then
                  yield "Newtonsoft.Json.Linq"
                  yield "Fleece.Newtonsoft"
                  yield "Falanx.Proto.Codec.Json"
              
              yield "Microsoft.FSharp.Core"
              yield "Microsoft.FSharp.Core.Operators"
              yield "Microsoft.FSharp.Collections"
              yield "Microsoft.FSharp.Control"
              yield "Microsoft.FSharp.Text" ]
            |> Set.ofSeq

        let nowarn =
            let createHashDirective directive values  =
                SynModuleDecl.HashDirective (ParsedHashDirective (directive, values, range.Zero), range.Zero)
            createHashDirective "nowarn" ["686"]

        let synTypes =   
            let rec loop (pt: Type) =
                [
                    for t in pt.GetNestedTypes() do
                        match t with 
                        | :? ProvidedUnion as pu ->
                            yield SynModuleDecl.CreateUnion(pu, typeof<TypeContainer>, knownNamespaces)
                        | :? ProvidedRecord as pr ->
                            match loop t with 
                            | [] -> ()
                            | children -> 
                                let info = SynComponentInfoRcd.Create(Ident.CreateLong t.Name)
                                yield SynModuleDecl.CreateNestedModule(info, children)
                            yield SynModuleDecl.CreateRecord(pr, typeof<TypeContainer>, knownNamespaces)
                        | :? ProvidedTypeDefinition as pe when pe.IsEnum -> 
                            yield SynModuleDecl.CreateEnum(pe)
                        | _ -> () 
                ]
            loop providedTypeRoot
            
        let cleanTypes = ASTCleaner.untypeSynModuleDecls synTypes
                             
        let parseTree =
            ParsedInput.CreateImplFile(
                ParsedImplFileInputRcd.CreateFs(outputFile)
                    .AddModule(
                        {SynModuleOrNamespaceRcd.CreateNamespace(Ident.CreateLong providedTypeRoot.Namespace) with IsRecursive = true}
                            .AddDeclarations ( [ yield openSystem
                                                 yield openSystemCollectionsGeneric
                                                 if codecs.Contains Binary then
                                                     yield openFrotoSerialization
                                                     yield openBinaryCodec
                                                     yield openBinaryCodecPrimitive
                                                 if codecs.Contains Json then
                                                     yield openJsonLinq
                                                     yield openFleeceNewtonsoft
                                                     yield openJsonCodec
                                                 yield nowarn
                                                 yield! cleanTypes] )
                    )
            )
        let formattedCode = formatAst parseTree

        let content =
            [ "//------------------------------------------------------------------------------"
              "//     This code was generated by the Falanx tool."
              "//     Changes to this file will be lost when code is regenerated."
              "//------------------------------------------------------------------------------"
              formattedCode ]
            |> String.concat (Environment.NewLine)
            |> normalizeBindingNames // make text deterministic

        let outDir = Path.GetDirectoryName(outputFile)
        if not (Directory.Exists(outDir)) then
            Directory.CreateDirectory(outDir) |> ignore

        IO.File.WriteAllText(outputFile, content)
        #if DEBUG
        printfn "%s" formattedCode
        #endif       