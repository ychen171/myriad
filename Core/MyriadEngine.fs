﻿namespace Myriad

open System 

type MyriadEngine(store : IMyriadStore, history : MyriadHistory) =
    do
        store.Initialize(history)

    new(store : IMyriadStore) = MyriadEngine(store, MyriadHistory.All())

    member x.MeasureBuilder with get() = store.GetMeasureBuilder()
    member x.PropertyBuilder with get() = store.GetPropertyBuilder()

    member x.GetDimension(key : String) = store.GetDimension(key)

    member x.GetDimensions() = store.GetDimensions()

    member x.GetMetadata() = store.GetMetadata()

    /// Query for any values not filtered by the context; if property key is empty, query over all keys
    member x.Query(propertyKey : String, context : Context) =
        store.GetAny(propertyKey, context)
    
    /// Get values that are the best match and not filtered by the context; if property key is empty, find over all keys
    member x.Get(propertyKey : String, context : Context) =
        let properties = store.GetMatches(propertyKey, context)
        properties 
        |> Seq.map (fun pair -> { Name = fst(pair).Key; Value = snd(pair).Value; Deprecated = fst(pair).Deprecated }) 
        |> Seq.toList

    member x.Get(propertyKey : String, asOf : DateTimeOffset) = store.GetProperty(propertyKey, asOf)

    member x.Put(operation : PropertyOperation) = store.PutProperty(operation)        

    member x.AddMeasure(``measure`` : Measure) = store.AddMeasure(``measure``) 

    member x.AddDimension(dimensionName : String) = store.AddDimension(dimensionName) 

    member x.SetDimensionOrder(dimensions : Dimension list) = store.SetDimensionOrder(dimensions)