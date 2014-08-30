﻿module SwiftSharp.SwiftCompiler

// #r "../Lib/IKVM.Reflection.dll";;

open System
open System.Text
open System.Collections.Generic

open SwiftParser

open IKVM.Reflection
open IKVM.Reflection.Emit

type Config =
    {
        InputUrls: string list
        OutputPath: string
        References: string list
    }

type ClrType = IKVM.Reflection.Type

type DefinedClrType = TypeBuilder * (GenericTypeParameterBuilder array)

type TypeId = (string * int) list

let swiftToId ((n, g) : SwiftTypeElement) : TypeId = [(n, g.Length)]
let rec clrToId (clrType : ClrType) : TypeId =
    let g =
        if clrType.ContainsGenericParameters then
            (clrType.GetGenericArguments ()).Length
        else 0
    let ti = clrType.Name.IndexOf ('`')
    let name = if ti > 0 then clrType.Name.Substring (0, ti) else clrType.Name
    if clrType.IsNested then
        let nid = clrToId clrType.DeclaringType
        List.append nid [(name, g)]
    else
        [(name, g)]

type TypeMap = Dictionary<TypeId, ClrType>

type Env (config) =
    let dir = System.IO.Path.GetDirectoryName (config.OutputPath)
    let name = new AssemblyName (System.IO.Path.GetFileNameWithoutExtension (config.OutputPath))
    let defaultNamespace = name.Name
    let u = new Universe ()
    let dirs = dir :: (config.References |> List.map (fun x -> System.IO.Path.GetDirectoryName (x)))
    do
        u.add_AssemblyResolve (new ResolveEventHandler (fun sender (e : ResolveEventArgs) ->
            let n = new AssemblyName (e.Name)
            match dirs |> Seq.map (fun d -> System.IO.Path.Combine (d, n.Name + ".dll")) |> Seq.tryFind (fun p -> System.IO.File.Exists (p)) with
            | Some p -> u.LoadFile (p)
            | _ -> failwith (sprintf "Cannot find %A" e.Name)
            ))

    let refs = config.References |> List.map u.LoadFile
    let mscorlib = refs |> List.find (fun x -> x.GetType ("System.String") <> null)
    let monotouch = refs |> List.tryFind (fun x -> x.GetType ("MonoTouch.Foundation.NSString") <> null)
    let errors = new ResizeArray<string> ()

    //
    // Add basic types
    //
    let stringType = mscorlib.GetType ("System.String")
    let intType = mscorlib.GetType ("System.Int32")
    let voidType = mscorlib.GetType ("System.Void")
    let objectType = mscorlib.GetType ("System.Object")
    let exportAttributeType = match monotouch with | Some x -> Some (x.GetType ("MonoTouch.Foundation.ExportAttribute")) | _ -> None
    let registerAttributeType = match monotouch with | Some x -> Some (x.GetType ("MonoTouch.Foundation.RegisterAttribute")) | _ -> None
    let asm = u.DefineDynamicAssembly (name, AssemblyBuilderAccess.Save, dir)
    let modl = asm.DefineDynamicModule (name.Name, config.OutputPath)
    do
        refs
        |> List.iter (fun a ->
            modl.__AddAssemblyReference (a.GetName (), a))

    //
    // Read the namespaces
    //
    let namespaces = new Dictionary<string list, TypeMap> ()
    do
        refs
        |> List.iter (fun a ->
            a.GetTypes () |> Array.iter (fun t ->
                let k = if t.Namespace <> null then t.Namespace.Split ('.') |> Array.toList else []
                let ns =
                    match namespaces.TryGetValue (k) with
                    | (true, x) -> x
                    | (false, _) ->
                        let x = new TypeMap ()
                        namespaces.Add (k, x)
                        x
                if t.IsPublic then
                    ns.Add (clrToId t, t)))

    let definedTypes = new Dictionary<TypeId, DefinedClrType> ()

    let coreTypes = new TypeMap ()

    let learnType id t =
        coreTypes.[[id]] <- t

    do
        learnType ("AnyObject", 0) objectType
        learnType ("Int", 0) intType
        learnType ("String", 0) stringType
        learnType ("Void", 0) voidType
        learnType ("Dictionary", 2) (mscorlib.GetType ("System.Collections.Generic.Dictionary`2"))
        learnType ("Array", 1) (mscorlib.GetType ("System.Collections.Generic.List`1"))
        learnType ("Action", 1) (mscorlib.GetType ("System.Action`1"))


    member this.Assembly = asm

    member this.CoreTypes = coreTypes
    member this.VoidType = voidType
    member this.ObjectType = objectType
    member this.StringType = stringType
    member this.ExportAttributeType = exportAttributeType

    member this.DefinedTypes = definedTypes

    member this.DefineType name generics attribs (parent : ClrType) =
        let t = modl.DefineType (defaultNamespace + "." + name, attribs, parent)
        let g = 
            match generics with
            | [] -> [||]
            | _ -> t.DefineGenericParameters (generics |> List.toArray)
        let id = [(name, g.Length)]
        let d = (t, g)
        definedTypes.[id] <- d
        (t, g)

    member this.GetNamespace (parts) =
        match namespaces.TryGetValue (parts) with
        | (true, ns) -> ns
        | (false, _) ->
            match namespaces.TryGetValue ("MonoTouch" :: parts) with
            | (true, ns) -> ns
            | (false, _) -> failwith (sprintf "Cannot find namespace %A" parts)

    member this.GetOperatorPrecedence op = failwith (sprintf "Unknown operator %s" op)

type TranslationUnit (env : Env, stmts : Statement list) =
    let typeAliases = new TypeMap ()
    let importedTypes = new TypeMap ()
    do
        stmts
        |> List.iter (function
            | DeclarationStatement(ImportDeclaration path) ->
                let ns = env.GetNamespace path
                ns |> Seq.iter (fun x -> importedTypes.Add (x.Key, x.Value))
            | _ -> ())

    member this.Env = env
    member this.Statements = stmts

    member private this.LookupKnownType id =
        match typeAliases.TryGetValue id with
        | (true, t) -> Some t
        | _ ->
            match env.DefinedTypes.TryGetValue id with
            | (true, (t, g)) -> Some (t :> ClrType)
            | _ ->
                match importedTypes.TryGetValue id with
                | (true, t) -> Some t
                | _ ->
                    match env.CoreTypes.TryGetValue id with
                    | (true, t) -> Some t
                    | _ -> None

    member this.DefineType = env.DefineType

    member this.DefineTypeAlias (name, swiftType) =
        let clrType = this.GetClrType (swiftType)
        typeAliases.[[(name, 0)]] <- clrType
        clrType

    member this.FindClrType (SwiftType es : SwiftType) =
        // TODO: This ignores nested types
        let e = es.Head
        let id = e |> swiftToId
        match this.LookupKnownType id, id with
        | Some t, [(_, 0)] -> Some t
        | Some t, _ ->
            // Great, let's try to make a concrete one
            let targs = snd e |> Seq.map this.GetClrType |> Seq.toArray
            Some (t.MakeGenericType (targs))
        | _ -> None

    member this.FindClrType (name : string) =
        this.FindClrType (SwiftType [(name, [])])

    member this.GetClrType (swiftType) =
        match this.FindClrType swiftType with
        | Some x -> x
        | _ -> failwith (sprintf "GetClrType failed for %A" swiftType)

    member this.GetClrTypeOrVoid optionalSwiftType =
        match optionalSwiftType with
        | Some x -> this.GetClrType x
        | _ -> env.VoidType

    member this.InferType (expr) =
        match expr with
        | _ -> failwith (sprintf "Cannot infer type of: %A" expr)

let notSupported x = failwith (sprintf "Not supported: %A" x)

let omg msg x = failwith (sprintf msg x)

let pass msg x = (sprintf msg x) |> ignore

let compileMethod (tu : TranslationUnit) ((typ, gps) : DefinedClrType) (methodName : string) (ps : (ParameterBuilder * ClrType) list) (body : Statement list) (il : ILGenerator) =

    let cid = ref 1
    let locals = ref []

    let declareLocal name clrType =
        let loc = il.DeclareLocal clrType
        loc.SetLocalSymInfo (name)
        locals := (name, loc) :: !locals
        loc

    let typeof e =
        match e with
        | Str _ -> tu.Env.StringType
        | Variable name ->
            match !locals |> List.tryFind (fun (n,_) -> n = name) with
            | Some (_, loc) -> loc.LocalType
            | None ->
                match ps |> List.tryFind (fun (x, _) -> x.Name = name) with
                | Some (_,t) -> t
                | _ -> omg "Can't find the type of variable %s" name
        | Funcall (Variable name, args) ->
            match tu.FindClrType (name) with
            | Some x -> x
            | _ -> failwith (sprintf "Don't know the type of funcall %A" name)
        | _ -> failwith (sprintf "Don't know the type of %A" e)

    let findCtor (typ : ClrType) (args : Argument list) =
        match typ.GetConstructors () |> Array.filter (fun x -> (x.GetParameters ()).Length = args.Length) with
        | [||] -> None
        | [|x|] -> Some x
        | xs ->
            match tu.Env.ExportAttributeType with
            | Some ex ->
                let mats = xs |> Array.map (fun x -> (x, x.__GetCustomAttributes (ex, true)))
                omg "I can't decide on a ctor %A" (mats, xs)
            | _ ->
                let pts = args |> Seq.map (snd >> typeof) |> Seq.toArray
                omg "Too many overloads of init %A" (pts, xs)

    let findMethod (typ : ClrType) name (args : Argument list) =
        match typ.GetMethods () |> Array.filter (fun x -> x.Name = name && (x.GetParameters ()).Length = args.Length) with
        | [||] -> None
        | [|x|] -> Some x
        | x -> omg "Too many overloads of %s" name 

    let rec eval e =
        match e with
        | Str s -> il.Emit (OpCodes.Ldstr, s)
        | Variable name ->
            match !locals |> List.tryFind (fun (n,_) -> n = name) with
            | Some (_, loc) -> il.Emit (OpCodes.Ldloc, loc.LocalIndex)
            | None ->
                match ps |> List.tryFindIndex (fun (x,_) -> x.Name = name) with
                | Some i -> il.Emit (OpCodes.Ldarg, i)
                | _ -> omg "Can't find variable %s for eval" name
        | Closure (head, body) ->
            let ctypeName = sprintf "%sClosure%d" methodName !cid
            cid := !cid + 1
            let nt = typ.DefineNestedType (ctypeName)
            nt.CreateType () |> ignore
            pass "Closure, closure %A" head
        | Funcall (Variable name, args) ->
            match tu.FindClrType (name) with
            | Some x ->
                match findCtor x args with
                | None -> omg "Couldn't find constructor for %A" args
                | Some ctor ->
                    args |> List.iter (snd >> eval)
                    il.Emit (OpCodes.Newobj, ctor)
            | _ ->
                match findMethod typ name args with
                | None -> omg "Couldn't find method for %A" typ
                | Some meth ->
                    args |> List.iter (snd >> eval)
                    il.EmitCall (OpCodes.Callvirt, meth, [||])
        | _ -> failwith (sprintf "Can't eval %A" e)

    let assign left right =
        match left with
        | Member (Some (Variable "self"), name) ->
            match typ.GetMember (name) with
            | null | [||] -> failwith (sprintf "Can't find member %A" name)
            | [|:? FieldBuilder as field|] ->
                eval right
                il.Emit (OpCodes.Ldloc_0);
                il.Emit (OpCodes.Stfld, field)
            | [|m|] -> failwith (sprintf "Don't know how to assign %A" m)
            | _ -> failwith (sprintf "Ambiguous member %A" name)
        | _ -> pass "Can't assign %A" left

    let rec run s =
        match s with
        | ExpressionStatement e ->
            match (flatten e tu.Env.GetOperatorPrecedence) with
            | Binary (left, OpBinary ("=", right)) -> assign left right
            | x -> eval x
        | DeclarationStatement (ConstantDeclaration [(IdentifierPattern (name, None), Some e)]) ->
            let loc = declareLocal name (typeof e)
            loc.SetLocalSymInfo (name)
            eval e
            il.Emit (OpCodes.Stloc, loc.LocalIndex)
        | x -> failwith (sprintf "Don't know how to compile statement %A" x)

    body |> List.iter run

let declareField (tu : TranslationUnit) (typ : DefinedClrType) decl =

    let getType optType optInit =
        match optType with
        | Some t -> tu.GetClrType (t)
        | _ ->
            match optInit with
            | Some e -> tu.InferType (e)
            | _ -> failwith "let statements must have a type or initializer"                

    match decl with
        | ConstantDeclaration specs ->
            let attribs = FieldAttributes.Public
            let builders = specs |> List.map (function
                | (IdentifierPattern (name, ot), oi) ->
                    let tt = fst typ
                    tt.DefineField (name, getType ot oi, attribs)
                | x -> notSupported x)                
            Some (fun () -> ())
        | _ -> None

let declareMethod (tu : TranslationUnit) (typ : DefinedClrType) decl =
    match decl with
        | FunctionDeclaration (dspecs, name : string, parameters: Parameter list list, res, body) ->
            if parameters.Length > 1 then failwith "Curried function declarations not supported"
            let returnType =
                match res with
                | Some (resAttrs, resType) -> tu.GetClrType (resType)
                | None -> tu.Env.VoidType
            let paramTypes =
                parameters.Head
                |> Seq.map (function
                    | (a,e,l,Some t,d) -> tu.GetClrType (t)
                    | _ -> tu.Env.ObjectType)
                |> Seq.toArray
            let attribs = MethodAttributes.Public
            let builder = (fst typ).DefineMethod (name, attribs, returnType, paramTypes)
            let ps = parameters.Head |> List.mapi (fun i (_, _, ploc, _, _) -> builder.DefineParameter (i + 1, ParameterAttributes.None, ploc))
            let psts = (List.zip ps (paramTypes |> Array.toList))
            Some (fun () -> compileMethod tu typ name psts body (builder.GetILGenerator ()))
        | InitializerDeclaration (parameters: Parameter list, body) ->
            let paramTypes =
                parameters
                |> Seq.map (function
                    | (a,e,l,Some t,d) -> tu.GetClrType (t)
                    | _ -> tu.Env.ObjectType)
                |> Seq.toArray
            let attribs = MethodAttributes.Public
            let builder = (fst typ).DefineConstructor (attribs, CallingConventions.Standard, paramTypes)
            let ps = parameters |> List.mapi (fun i (_, _, ploc, _, _) -> builder.DefineParameter (i + 1, ParameterAttributes.None, ploc))
            let psts = (List.zip ps (paramTypes |> Array.toList))
            Some (fun () -> compileMethod tu typ "init" psts body (builder.GetILGenerator ()))
        | _ -> None

type DeclaredType = DefinedClrType * (Declaration list)

let declareType (tu : TranslationUnit) stmt : DeclaredType list =
    match stmt with
    | DeclarationStatement (ClassDeclaration (name, generics, inheritance, decls) as d) -> [(tu.DefineType name generics TypeAttributes.Public null, decls)]
    | DeclarationStatement (UnionEnumDeclaration (name, generics, inheritance, cases) as d) ->
        let tb, gbs = tu.DefineType name generics (TypeAttributes.Public ||| TypeAttributes.Abstract) null
        let cbs : DeclaredType list = cases |> List.collect (fun ccases ->
            ccases |> List.map (fun (cn, oct) ->
                let tattribs = TypeAttributes.Class ||| TypeAttributes.Public
                let fattribs = FieldAttributes.Public
                let cb, cgbs = tu.DefineType (name + cn) [] tattribs (tb :> ClrType)
                match oct with
                | Some (SwiftType [("Tuple", ts)]) ->
                    ts |> List.iteri (fun i tt ->
                        let fb = cb.DefineField (sprintf "Item%d" (i+1), tu.GetClrType (tt), fattribs)
                        ())
                | _ -> ()
                ((cb, cgbs), [])))
        ((tb, gbs), []) :: cbs
    | DeclarationStatement (TypealiasDeclaration (name, typ)) ->
        let ct = tu.DefineTypeAlias (name, typ)
        []
    | _ -> []


let compile config =
    let env = new Env (config)

    // Parse each translation unit
    let tus = config.InputUrls |> List.choose parseFile |> List.map (fun x -> new TranslationUnit (env, x))

    // First pass: Declare the types
    let typeDecls = tus |> List.map (fun x -> (x, x.Statements |> List.collect (declareType x)))

    // Second pass: Declare methods - these state their types explicitely
    let methodCompilers =
        typeDecls |> List.collect (fun (tu, tdecls) ->
            tdecls |> List.collect (fun (typ, decls) ->
                decls |> List.choose (declareMethod tu typ)))

    // Third pass: Declare fields and properties
    let fieldCompilers =
        typeDecls |> List.collect (fun (tu, tdecls) ->
            tdecls |> List.collect (fun (typ, decls) ->
                decls |> List.choose (declareField tu typ)))

    // Bake the types
    let types =
        env.DefinedTypes.Values
        |> Seq.map (fun (tb, gbs) -> if tb.IsCreated () then tb :> ClrType else tb.CreateType ())
        |> Seq.toArray

    // Fourth pass: Compile code, finally
    for fc in fieldCompilers do fc ()
    for mc in methodCompilers do mc ()

    // Save it
    env.Assembly.Save (config.OutputPath)
    types


let compileFile file =
    let config =
        {
            InputUrls = [file]
            OutputPath = System.IO.Path.ChangeExtension (file, ".dll")
            References = [ typeof<String>.Assembly.Location ]
        }
    compile config


//compileFile "/Users/fak/Dropbox/Projects/SwiftSharp/SwiftSharp.Test/TestFiles/SODAClient.swift"


