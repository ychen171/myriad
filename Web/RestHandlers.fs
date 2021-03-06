﻿namespace Myriad.Web

open System
open System.Collections.Specialized
open System.Diagnostics
open System.Globalization
open System.Net
open System.Text
open System.Text.RegularExpressions
open System.Web
open System.Xml

open NLog 

open Suave
open Suave.Http
open Suave.RequestErrors
open Suave.Operators
open Suave.Successful
open Suave.Writers

open Newtonsoft.Json

open Myriad

module RestHandlers =
    let logger = LogManager.GetCurrentClassLogger()

    let requestMappings = AppConfiguration.getRequestMappings()

    let setAccessControl =
        Writers.setHeader "Access-Control-Allow-Origin" "*" 
        >=> Writers.setHeader "Access-Control-Allow-Headers" "Origin, X-Requested-With, Content-Type, Accept, Key"

    let setCustomHeaders (ctx : HttpContext) =
        let getHeader (key : String) = 
            ctx.request.headers |> List.tryFind (fun kv -> fst(kv).Equals(key, StringComparison.InvariantCultureIgnoreCase))
        let getValue (mapping : RequestMapping) =
            let current = getHeader mapping.Source
            if current.IsNone then
                None
            else
                let m = Regex.Match(snd current.Value, mapping.Value)            
                if m.Success then Some(mapping.Target, m.Groups.[1].ToString().Replace(",", "")) else None
        let applyMapping(mapping : RequestMapping) =
            match mapping.Type with
            | RequestMappingType.Set -> Some(mapping.Target, mapping.Value)
            | RequestMappingType.Regex -> getValue mapping
        let newKeys = requestMappings |> List.choose applyMapping     
        let headers' = List.concat [ newKeys; ctx.request.headers ] |> Seq.distinctBy (fun kv -> fst kv) |> Seq.toList
        { ctx with request = { ctx.request with headers = headers' } } |> succeed

    let private fromJson<'a> json =
        match json with
        | json when String.IsNullOrEmpty(json) -> None
        | _ -> Some(JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a)

    let private getRawForm(req : HttpRequest) = 
        let getString rawForm = Encoding.UTF8.GetString(rawForm)
        req.rawForm |> getString 

    let private fromRequest<'a> (req : HttpRequest) = 
        getRawForm req |> fromJson<'a>

    let private getRequestParameters (request : HttpRequest) =
        let parameters = NameValueCollection(StringComparer.InvariantCultureIgnoreCase)        
        parameters.Add(HttpUtility.ParseQueryString(request.rawQuery))        
        request.headers |> List.iter (fun kv -> parameters.Add(fst kv, snd kv))                
        parameters

    let private getAsOf (kv : NameValueCollection) =
        let value = kv.["asOf"]     // Compare is case-insensitive
        if value <> null then            
            let success, result = DateTimeOffset.TryParse(value, null, DateTimeStyles.AssumeUniversal)
            if success then result else DateTimeOffset.UtcNow
        else
            DateTimeOffset.UtcNow

    let private getValues (kv : NameValueCollection) (key : String) =
        match kv.[key] with
        | p when not(String.IsNullOrEmpty(p)) -> p.Split([|','|]) 
        | _ -> [| "" |]

    let private getPropertyKeys(kv : NameValueCollection) = getValues kv "property"

    let private getContext (getDimension : String -> Dimension option) (kv : NameValueCollection) =
        let getValue key =
            let values = getValues kv key         
            match values with
            | [||] -> ""
            | [| v |] -> v
            | _ -> values.[values.Length - 1]   // Last wins
        let getMeasure(key) = 
            let dimension = getDimension(key)
            match dimension with
            | None -> None
            | Some(d) when d.Name = "Property" -> None
            | Some(d) -> Some( { Dimension = dimension.Value; Value = getValue key } )
        let measures = kv.AllKeys |> Seq.choose getMeasure |> Set.ofSeq
        { AsOf = getAsOf kv; Measures = measures }

    let private getResponseString<'T> (kv : NameValueCollection) (response : 'T) =
        let format = [ kv.["format"]; kv.["as"] ] |> List.tryFind (fun f -> not(String.IsNullOrEmpty f))
        match format with
        | None -> "text/json", JsonConvert.SerializeObject(response)
        | Some(f) when f.ToLower() = "json" -> "text/json", JsonConvert.SerializeObject(response)        
        | Some(f) when f.ToLower() = "xml" -> "text/xml", XmlConvert.SerializeObject(response)
        | Some(f) when f.ToLower() = "text" -> "text/raw", response.ToString()
        | Some(f) -> raise(ArgumentException("Unknown format [" + f + "]"))

    let handleRequest (x : HttpContext) (handler : Guid -> NameValueCollection -> (String -> WebPart) * String * String) =
        async { 
            let requestId = Guid.NewGuid()
            try
                logger.Info("RECV: [{0}] [{1}]", requestId, x.request.url)
                logger.Debug("RECV: [{0}] [{1}]", requestId, fromRequest x.request)

                let kv = getRequestParameters x.request
                let webResponse, contentType, message = handler requestId kv
                logger.Info("SEND: [{0}] [{1}] {2} Length: {3}", requestId, x.request.url, contentType, message.Length)
                logger.Debug("SEND: [{0}] [{1}]", requestId, message)                                

                let! ctx = Writers.setMimeType contentType x                                
                return! webResponse message ctx.Value
            with 
            | :? ArgumentException as ex -> 
                logger.Error("UNPROCESSABLE_ENTITY: [{0}] [{1}] {2}\r\n{3}", requestId, x.request.url, x.request.rawQuery, ex.ToString())
                let! ctx = Writers.setMimeType "text/plain" x
                return! UNPROCESSABLE_ENTITY (ex.Message) ctx.Value
            | ex -> 
                logger.Error("BAD_REQUEST: [{0}] [{1}] {2}\r\n{3}", requestId, x.request.url, x.request.rawQuery, ex.ToString())
                let! ctx = Writers.setMimeType "text/plain" x
                return! BAD_REQUEST (ex.Message) ctx.Value
        }        

    let Root (startTime : DateTimeOffset) (x : HttpContext) =
        let appInfo (requestId : Guid) (kv : NameValueCollection) =                        
            logger.Info("RECV: [{0}] Getting application info", requestId)
            let uptime = XmlConvert.ToString(DateTimeOffset.UtcNow.Subtract startTime)
            let response = { Name = CurrentProcess.GetTitle(); 
                             Version = CurrentProcess.GetVersionAsString(); 
                             ProcessId = CurrentProcess.GetProcessId(); 
                             StartTime = startTime; 
                             UpTime = uptime }
            let contentType, message = getResponseString kv response
            OK, contentType, message
        handleRequest x appInfo
        
    /// Provides an ordered list of dimensions 
    let GetDimensions (engine : MyriadEngine) (x : HttpContext) = 
        let getDimensions (requestId : Guid) (kv : NameValueCollection) =            
            let response = engine.GetDimensions() |> List.map (fun d -> d.Name)
            let contentType, message = getResponseString kv response
            OK, contentType, message
        handleRequest x getDimensions

    /// Provides a view over both properties and dimensions
    let GetMetadata (engine : MyriadEngine) (x : HttpContext) = 
        let getMetadata (requestId : Guid) (kv : NameValueCollection) =            
            let response = engine.GetMetadata()
            let contentType, message = getResponseString kv response
            OK, contentType, message
        handleRequest x getMetadata

    /// Query -> JSON w/ context
    let Query (engine : MyriadEngine) (x : HttpContext) =
        let query (requestId : Guid) (kv : NameValueCollection) =
            let context = getContext (engine.GetDimension) kv
            logger.Info("RECV: [{0}] Context: {1}", requestId, context)
            let properties = getPropertyKeys(kv)
                             |> Seq.map (fun p -> engine.Query(p, context))
                             |> Seq.concat            
            let dimensions = engine.GetDimensions() 
            let dataRows = properties |> Seq.mapi (fun i p -> Cluster.ToMap(fst(p).Key, fst(p).Deprecated, snd(p), dimensions, i))

            let response = { data = dataRows }            
            let contentType, message = getResponseString kv response
            OK, contentType, message
        handleRequest x query

    /// GET -> URL properties with dimensions name=value
    let Get (engine : MyriadEngine) (x : HttpContext) =
        let get (requestId : Guid) (kv : NameValueCollection) =            
            let context = getContext (engine.GetDimension) kv
            logger.Info("RECV: [{0}] Context: {1}", requestId, context)
            let properties = getPropertyKeys(kv)
                             |> Seq.map (fun p -> engine.Get(p, context))
                             |> Seq.concat
                             |> Seq.toList
            let response = { MyriadGetResponse.Requested = DateTimeOffset.UtcNow; Context = context; Properties = properties }            
            let contentType, message = getResponseString kv response
            OK, contentType, message
        handleRequest x get

    let GetProperty (engine : MyriadEngine) (x : HttpContext) =
        let getProperty (requestId : Guid) (kv : NameValueCollection) =
            let asOf = getAsOf kv
            let properties = getPropertyKeys(kv) |> Seq.choose (fun p -> engine.Get(p, asOf))
            let response = { Requested = DateTimeOffset.UtcNow; Properties = properties }
            let contentType, message = getResponseString kv response       
            OK, contentType, message
        handleRequest x getProperty
        
    /// PUT property operation (JSON data) -> property
    let PutProperty (engine : MyriadEngine) (x : HttpContext) =
        let putProperty (requestId : Guid) (kv : NameValueCollection) =
            let property = fromRequest<PropertyOperation>(x.request)
            if property.IsNone then
                let contentType, message = getResponseString kv "PropertyOperation could not be read."
                BAD_REQUEST, contentType, message
            else
                let newProperty = engine.Put(property.Value)
                let response = { Requested = DateTimeOffset.UtcNow; Property = newProperty }
                let contentType, message = getResponseString kv response       
                OK, contentType, message
        handleRequest x putProperty                                

    /// PUT new dimension+value (measure) -> Dimension * string list
    let PutMeasure (engine : MyriadEngine) (x : HttpContext) =
        let putMeasure (requestId : Guid) (kv : NameValueCollection) =
            let ``measure`` = fromRequest<Measure>(x.request)
            if ``measure``.IsNone then
                BAD_REQUEST, "text", "Measure could not be read."
            else
                logger.Info("Adding measure [{0}]", ``measure``.Value.ToString())
                let response = engine.AddMeasure(``measure``.Value)
                if response.IsNone then
                    BAD_REQUEST, "text", "Measure could not be added."
                else
                    let contentType, message = getResponseString kv response.Value                    
                    OK, contentType, message         
        handleRequest x putMeasure

    /// PUT new dimension(s) -> Dimension list
    let PutDimension (engine : MyriadEngine) (x : HttpContext) =
        let putDimension (requestId : Guid) (kv : NameValueCollection) =
            let dimensionNames = getValues kv "dimension"
            if dimensionNames |> Seq.length = 0 then
                BAD_REQUEST, "text", "No dimensions were sent."
            else
                logger.Info("Adding dimensions [{0}]", String.Join(", ", dimensionNames))                
                let response = dimensionNames |> Seq.map (fun n -> engine.AddDimension(n)) |> Seq.toList
                let contentType, message = getResponseString kv response
                OK, contentType, message         
        handleRequest x putDimension

    /// PUT new dimension(s) -> Dimension list
    let PutDimensionOrder (engine : MyriadEngine) (x : HttpContext) =
        let putDimensionOrder (requestId : Guid) (kv : NameValueCollection) =
            let dimensionsOption = fromRequest<Dimension list>(x.request)
            if dimensionsOption.IsNone then
                BAD_REQUEST, "text", "Dimension list could not be read."
            else
                let dimensions = dimensionsOption.Value
                logger.Info("Setting dimension order: [{0}]", String.Join(", ", dimensions))
                let response = engine.SetDimensionOrder(dimensions)
                let contentType, message = getResponseString kv response
                OK, contentType, message         
        handleRequest x putDimensionOrder
