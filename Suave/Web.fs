﻿module Suave.Web

open Utils

open System
open System.IO

open System.Text
open System.Diagnostics
open System.Threading
open System.Security.Permissions
open System.Security.Principal
open System.Collections.Generic

open Http

let rec readTillEOL(stream: Stream, buff: byte[], count: int) =
   async{
       
       let! inp = stream.AsyncRead(1)
       
       if count>0 && buff.[count-1] = EOL.[0] && inp.[0] = EOL.[1] then
          return (count-1)
       else

          buff.[count] <- inp.[0]
          return! readTillEOL(stream,buff,count + 1)
   }

let read_until (marker:byte array) f (stream:Stream)  = 
    
    let buffer = Array.create marker.Length (byte(0))
    
    let rec loop index = async {
        if index < Array.length marker then 
            let! byte = stream.AsyncRead(1)
            if byte.Length > 0 then 
                buffer.[index] <- byte.[0]
                if byte.[0] = marker.[index] then
                    do! loop (index + 1)  
                else 
                    do! f buffer (index+1)
                    do! loop 0
    }
    loop 0    
    
let toString ( buff: byte[], index:int, count:int) =
    Encoding.ASCII.GetString(buff,index,count)
    
let max_buffer = 1024    
    
let read_line stream = async {
    let buf = Array.zeroCreate max_buffer 
    let! count = readTillEOL(stream,buf,0)  
    return toString(buf, 0, count)  
}

let read_headers stream = async {
    let eof = ref false
    let headers = new Dictionary<string,string>()
    while not(!eof) do
        let buf = Array.zeroCreate max_buffer
        let! count = readTillEOL(stream,buf,0)
        if count <> 0 then
            let line = toString(buf, 0, count)
            let indexOfColon = line.IndexOf(':'); 
            headers.Add (line.Substring(0,indexOfColon).ToLower(),line.Substring(indexOfColon+1).TrimStart())
        else
            eof := true
    return headers
}
    
open Suave.Types

let empty_query_string () = new Dictionary<string,string>()

let query (x:HttpRequest) = x.Query
let form (x:HttpRequest) = x.Form

let parse_data (s:string) = 
    
    let param = empty_query_string ()
    s.Split('&')
    |> Array.iter 
        (fun (k:string) -> k.Split('=')  
                            |> (fun d -> if d.Length = 2 then param.Add(d.[0],System.Web.HttpUtility.UrlDecode(d.[1])))) 
    param
    
let parse_url (line:string) =
    let parts = line.Split(' ')
    if(parts.Length <2 || parts.Length>3) then failwith "400"
    let indexOfMark = parts.[1].IndexOf('?')
    
    if (indexOfMark>0) then
        let raw_query = parts.[1].Substring(indexOfMark+1)
        (parts.[0],parts.[1].Substring(0,indexOfMark),parse_data raw_query, "?" + raw_query)
    else 
        (parts.[0],parts.[1],empty_query_string (), String.Empty)
    
let read_post_data (stream:Stream) (bytes : int) = 
    stream.AsyncRead(bytes)
    
let parse_cookie (s:string) =
    s.Split(';') 
    |> Array.map (fun (x:string) -> 
                        let parts = x.Split('='); 
                        (parts.[0],parts.[1]))
                        
let parse_key_value_pairs arr =
    let dict = new Dictionary<string,string>()
    arr 
    |> Array.iter (fun (x:String) -> 
                    let parts = x.Split('=');
                    dict.Add(parts.[0],parts.[1]))
    dict
    
let header_params (header:string option) =
    match header with
    |Some(x) -> let parts = x.Split(';')|> Array.map (fun x -> x.TrimStart())
                parse_key_value_pairs (Array.sub parts 1 (parts.Length-1))
                
    |_ -> failwith "did not found header: %s." header
            
let parse_multipart (stream:Stream) boundary (request:HttpRequest) = 
    let rec loop boundary = 
        async {
        
        let! firstline = read_line stream
                
        if not(firstline.Equals("--")) then 
            
            let! part_headers = read_headers stream 
            let content_disposition = look_up part_headers "content-disposition"
            
            let fieldname = 
                (header_params content_disposition) ? name |> opt
                
            let content_type = look_up part_headers "content-type"
            
            match content_type with
            |Some(x) when x.StartsWith("multipart/mixed") 
                -> 
                    let subboundary = "--" + x.Substring(x.IndexOf('=')+1).TrimStart()
                    do! loop subboundary  
            |Some(x) 
                -> 
                    let temp_file_name = Path.GetTempFileName()
                    use temp_file = new FileStream(temp_file_name,FileMode.Truncate)
                    do! stream 
                        |> read_until (bytes("\r\n" + boundary)) (fun x y -> async { do! temp_file.AsyncWrite(x,0,y) }  )
                    let file_leght = temp_file.Length
                    temp_file.Close()
                    if  file_leght > int64(0) then
                        let filename = 
                            (header_params content_disposition) ? filename |> opt
                        request.Files.Add(new HttpUpload(fieldname,filename,content_type |> opt,temp_file_name))
                    else 
                        File.Delete(temp_file_name)
            |None-> 
                    use mem = new MemoryStream()
                    do! stream 
                        |> read_until (bytes("\r\n" + boundary)) (fun x y -> async { do! mem.AsyncWrite(x,0,y) }  )
                    
                    let byts = mem.ToArray()
                    request.Form.Add(fieldname, toString(byts,0,byts.Length))
            
            do! loop  boundary
    } 
    loop boundary   
     
let process_request proxyMode (stream:Stream) remoteip = async {
    
    let request = new HttpRequest()
    request.Stream <- stream
    request.RemoteAddress <- remoteip
    
    let! first_line = read_line stream 
    
    let (meth,url,query, raw_query) as q = parse_url (first_line)
    request.Url <- url
    request.Method <- meth
    request.Query <- query
    request.RawQuery <- raw_query
    
    let! headers = read_headers stream 
    request.Headers <- headers
    
    //wont continue parsing if on proxyMode with the intention of forwarding the stream as it is
    if not proxyMode then
        request.Headers 
        |> Seq.filter (fun x -> x.Key.Equals("cookie"))
        |> Seq.iter (fun x -> 
                        let cookie = parse_cookie x.Value; 
                        request.Cookies.Add (fst(cookie.[0]),cookie))
    
        if meth.Equals("POST") then 
            let content_enconding = headers.["content-type"]
            let content_length = Convert.ToInt32(headers.["content-length"])
        
            if content_enconding.StartsWith("application/x-www-form-urlencoded") then
                let! rawdata = read_post_data stream content_length
                let form_data = parse_data (toString (rawdata, 0, rawdata.Length))
                request.Form <- form_data
                request.RawForm <- rawdata
            elif content_enconding.StartsWith("multipart/form-data") then
                let boundary = "--" + content_enconding.Substring(content_enconding.IndexOf('=')+1).TrimStart()
                do! parse_multipart stream boundary request
            
    return request
} 

open System.Net    
open System.Net.Security
open System.Security.Authentication
open System.Security.Cryptography.X509Certificates;

open Types
    
let load_stream proto (stream: Stream)  = 
    match proto with
    |HTTP -> stream 
    |HTTPS(cert) -> 
                let sslStream = new SslStream(stream, true) 
                sslStream.AuthenticateAsServer(cert, false, SslProtocols.Default, true)
                sslStream :> Stream;

open System.Net.Sockets

let eval_action x r = 
    async { 
        try
            do! x 
        with ex -> 
            Log.log "action failed with:\n%A" ex
            do! (response 500 "Internal Error" (bytes(ex.ToString())) r)
    }

let request_loop webpart proto (processor: Stream -> String -> Async<HttpRequest>) (client:TcpClient) = 
    async {
        try
        
            let keep_alive = ref true
            use stream = 
                client.GetStream()
                |> load_stream proto
            
            let remote_endpoint = (client.Client.RemoteEndPoint :?> IPEndPoint)
            let ipaddr = remote_endpoint.Address.ToString()

            while !keep_alive do
                use! request = processor stream ipaddr 
                try
                    let p = webpart request //routing
                    match p with 
                    |Some(x) -> do! eval_action x request
                    |None -> ()
                with ex -> 
                    Log.log "routing request failed.\n%A" ex
                    do! (response 500 "Internal Error" (bytes(ex.ToString())) request)
                    
                stream.Flush()
                
                keep_alive := 
                    match request.Headers ? connection with 
                    |Some(x) when x.ToLower().Equals("keep-alive") -> true
                    |_ -> false
                
            client.Close()
        with | :? EndOfStreamException -> Log.log "client disconnected.\n"
             | ex -> Log.log "request failed.\n%A" ex
    }
    
let parallelize input f = input |> Seq.map (f)  |> Async.Parallel

open Suave.Tcp
   
let web_server bindings (webpart:WebPart) =
    bindings 
    |> Array.map (fun (proto,ip,port) -> tcp_ip_server (ip,port) (request_loop webpart proto (process_request false)))
    |> Async.Parallel
    |> Async.Ignore


