﻿namespace Myriad

open System
open System.Runtime.Serialization
open System.Xml
open System.Xml.Serialization

[<CustomEquality;CustomComparison>]
type Property = 
    { Key : String; Description : String; Deprecated : bool; Timestamp : Int64; Clusters : Cluster list }

    override x.Equals(yobj) = 
        match yobj with
        | :? Property as y -> (x.Key = y.Key && x.Timestamp = y.Timestamp)
        | _ -> false

    override x.GetHashCode() = hash(x)
    
    override x.ToString() =
        String.Format("'{0}' {1} clusters @ {2}", x.Key, Seq.length x.Clusters, x.Timestamp)
    
    interface IComparable with
        member x.CompareTo other = 
            match other with 
            | :? Property as y -> x.Timestamp.CompareTo(y.Timestamp)
            | _ -> invalidArg "other" "cannot compare value of different types" 

    interface IXmlSerializable with
        member x.GetSchema() = null
        member x.ReadXml(reader) = ignore()
        member x.WriteXml(writer) = x.WriteXml(writer)
                        
    member x.WriteXml(writer : XmlWriter) =             
        writer.WriteStartElement("Property")
        
        writer.WriteAttributeString("Key", x.Key)
        writer.WriteAttributeString("Deprecated", x.Deprecated.ToString())
        writer.WriteAttributeString("Timestamp", x.Timestamp.ToString())

        writer.WriteStartElement("Description")
        writer.WriteCData(x.Description)
        writer.WriteEndElement()

        x.Clusters |> List.iter (fun m -> m.WriteXml(writer))
        writer.WriteEndElement()

    static member Create(key : String, timestamp : Int64, clusters : Cluster list) =
        { Key = key; Description = ""; Deprecated = false; Timestamp = timestamp; Clusters = clusters }

    static member Create(key : String, description : String, deprecated : bool, timestamp : Int64, clusters : Cluster list) = 
        { Key = key; Description = description; Deprecated = deprecated; Timestamp = timestamp; Clusters = clusters }
