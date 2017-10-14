module fschat.app

open Suave
open Suave.Operators
open Suave.Filters
open Suave.Redirection
open Suave.Successful
open Suave.RequestErrors

// ---------------------------------
// Web app
// ---------------------------------
module internal AppState =

    type UserSession =
        {
            UserName: string
            // token
            Channels: string list // list of channels I'm in
        }

    type App =
        {
            Channels: string list
        }

// app state
    let app = { App.Channels = ["hardware"; "software"; "cats"] }
    let mutable userSession = { UserSession.UserName = "%username%"; Channels = ["cats"]}

let joinChannel chan =
    if not(AppState.userSession.Channels |> List.contains chan) then
        AppState.userSession <- { AppState.userSession with Channels = chan :: AppState.userSession.Channels}
    redirect "/channels"

let leaveChannel chan =
    // todo leave, redirect to channels list
    OK ("TBD leave channel " + chan)

let webApp: WebPart =
    choose [
        pathStarts "/channels" >=> choose [
            GET >=> path "/channels" >=> (Views.channels AppState.app.Channels |> Views.pageLayout "Channels" |> Html.renderHtmlDocument |> OK)
            GET >=> pathScan "/channels/%s/join" joinChannel
            GET >=> pathScan "/channels/%s/leave" leaveChannel
            GET >=> pathScan "/channels/%s" (fun chan ->
                 choose [
                     GET  >=> OK ("TBD view channel messages " + chan)
                     POST >=> OK ("TBD post message to " + chan)
                 ])                
        ]
        // GET >=>
        //     choose [
        //         route "/" >=> warbler (fun _ -> AppState.userSession |> razorHtmlView "Index")
        //     ]
        NOT_FOUND "Not Found"
    ]

let errorHandler (ex: System.Exception) =
    // FIXME clear response
    Suave.ServerErrors.INTERNAL_ERROR ex.Message