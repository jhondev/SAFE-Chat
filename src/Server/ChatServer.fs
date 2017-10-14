module ChatServer

open System

open Akka.Actor
open Akka.Streams
open Akka.Streams.Dsl
open Akkling
open Akkling.Streams

open ChannelFlow

type MaterializeFlow = Flow<Message,Uuid ChatClientMessage, Akka.NotUsed> -> UniqueKillSwitch

type ChannelInfo = {id: Uuid; name: string; topic: string; userCount: int; users: Uuid list}
type UserInfo = {id: Uuid; nick: string; email: string option; channels: ChannelInfo list}

module ServerState =

    type UserData = {
        id: Uuid
        nick: string
        email: string option
        mat: MaterializeFlow option
        channels: Map<Uuid, UniqueKillSwitch option>
    }

    /// Channel is a primary store for channel info and data
    type ChannelData = {
        id: Uuid
        name: string
        topic: string
        channelActor: IActorRef<Uuid ChannelMessage>
    }

    type ServerData = {
        channels: ChannelData list
        users: UserData list
    }

type ServerControlMessage =
    | List                                              // returns ChannelList
    | NewChannel of name: string                        // returns ChannelInfo
    | SetTopic of chan: Uuid * topic: string
    // RenameChan
    | FindChannel of name: string                       // return ChannelInfo
    | DropChannel of Uuid: Uuid
    // user specific commands
    | Connect of nick: string * mat: MaterializeFlow option  * channels: Uuid list   // return UserInfo
    | Disconnect of user: Uuid
    | Join of user: Uuid * channelName: string
    // | Nick of user: Uuid * newNick: string
    | Leave of user: Uuid * chanId: Uuid
    | GetUser of user: Uuid                             // returns UserInfo

    | UpdateState of (ServerState.ServerData -> ServerState.ServerData)
    | ReadState

type ServerReplyMessage =
    | ChannelList of ChannelInfo list
    | ChannelInfo of ChannelInfo
    | UserInfo of UserInfo
    | State of ServerState.ServerData
    | Error of string

module internal Helpers =
    open ServerState

    let updateChannels f serverState: ServerData =
        {serverState with channels = serverState.channels |> List.map f}

    let updateUsers f serverState: ServerData =
        {serverState with users = serverState.users |> List.map f}

    let updateChannel f chanId serverState: ServerData =
        let u chan = if chan.id = chanId then f chan else chan
        in
        updateChannels u serverState

    let byChanId id c = (c:ChannelData).id = id
    let byChanName name c = (c:ChannelData).name = name
    let byUserId id u = (u:UserData).id = id

    let setChannelTopic topic (chan: ChannelData) =
        {chan with topic = topic}

    let updateUser f userId serverState: ServerData =
        let u (user: UserData) = if user.id = userId then f user else user
        in
        updateUsers u serverState

    let addUserChan chanId ks (user: UserData) =
        {user with channels = user.channels |> Map.add chanId ks}

    let alreadyJoined channels channelName (u: UserData) =
        channels |> List.tryFind (byChanName channelName)
        |> function
        | Some ch when u.channels |> Map.containsKey ch.id -> true
        | _ -> false

    let leaveChan chanId (user: UserData) =
        {user with channels = user.channels |> Map.remove chanId}
    
    let getChannelInfo (data: ChannelData) =
        async {
            let! (users: Uuid list) = data.channelActor <? ListUsers
            return {id = data.id; name = data.name; topic = data.topic; userCount = users |> List.length; users = users}
        }
    let getChannelInfo0 (data: ChannelData) =
        {id = data.id; name = data.name; topic = data.topic; userCount = 0; users = []}

    let getUserInfo (data: UserData) (channels: ChannelData list) =
        let getChan ids =
            channels |> List.filter (fun chan -> ids |> Map.containsKey chan.id)
            |> List.map getChannelInfo0 // FIXME does not return userCount
        { id = data.id; nick = data.nick; email = data.email
          channels = data.channels |> getChan}

    module Async =
        let map f workflow = async {
            let! res = workflow
            return f res }

module ServerApi =
    open ServerState
    open Helpers

    // verifies the name is correct
    let isValidName (name: string) =
        (String.length name) > 0
        && Char.IsLetter name.[0]

    let listChannels state =
        async {
            let! channels = state.channels |> List.map getChannelInfo |> Async.Parallel
            return channels |> Array.toList
        }

    /// Creates a new channel or returns existing if channel already exists
    let addChannel createChannel name (state: ServerData) =
        match state.channels |> List.tryFind (byChanName name) with
        | Some chan ->
            Ok (state, chan)
        | _ when isValidName name ->
            let channelActor = createChannel name
            let newChan = {
                id = Uuid.New(); name = name; topic = ""; channelActor = channelActor }
            Ok ({state with channels = newChan::state.channels}, newChan)
        | _ ->
            Result.Error "Invalid channel name"

    let findChannel name (state: ServerData) =
        match state.channels |> List.tryFind (byChanName name) with
        | Some chan -> getChannelInfo0 chan |> Ok
        | _ -> Result.Error "Channel with such name not found"

    let findChannelEx name (state: ServerData) =
        match state.channels |> List.tryFind (byChanName name) with
        | Some chan -> getChannelInfo chan |> Async.map Ok
        | _ -> Result.Error "Channel with such name not found" |> async.Return

    let setTopic chanId newTopic state =
        Ok (state |> updateChannel (setChannelTopic newTopic) chanId)

    let private kickUser chanId (u: UserData) =
        match u.channels |> Map.tryFind chanId with
        | Some (Some ks) ->
            do ks.Shutdown()
            {u with channels = u.channels |> Map.remove chanId}
        | Some _ ->
            {u with channels = u.channels |> Map.remove chanId}
        | _ -> u

    let dropChannel chanId state =
        match state.channels |> List.tryFind (byChanId chanId) with
        | Some chan ->
            let newState = state |> updateUsers (kickUser chanId)
            in
            Ok {newState with channels = state.channels |> List.filter (not << byChanId chanId)}
        | _ -> Result.Error "Channel not found"        

    let connectUser (userId: Uuid option) nick channels (mat: MaterializeFlow option) state =
        match state.users |> List.exists(fun u -> u.nick = nick) with
        | true ->
            Result.Error "User with such nick already exists"
        | _ ->
            let newUserId = userId |> Option.defaultValue (Uuid.New())
            let newUser = {
                id = newUserId; nick = nick; email = None
                mat = mat
                channels = state.channels
                    |> List.filter(fun chan -> channels |> List.contains chan.id)
                    |> List.map (fun chan ->
                        let ks = mat |> Option.map (fun m -> m <| createPartyFlow chan.channelActor newUserId)
                        chan.id, ks
                    )
                    |> Map.ofList
            }
            Ok ({state with users = newUser :: state.users}, getUserInfo newUser state.channels)

    let disconnect userId state =
        match state.users |> List.tryFind (fun u -> u.id = userId) with
        | None ->
            Result.Error "User with such id not found"
        | Some user ->
            user.channels |> Map.iter(fun _ ks ->
                match ks with
                | Some killSwitch -> killSwitch.Shutdown()
                | _ -> ()
            )
            Result.Ok {state with users = state.users |> List.filter(fun u -> u.id <> userId)}

    let join userId channelName createChannel state =
        match state.users |> List.tryFind (fun u -> u.id = userId) with
        | None ->
            Result.Error "User with such id not found"
        | Some user when user |> alreadyJoined state.channels channelName ->
            Result.Error "User already joined this channel"
        | Some user ->
            match addChannel createChannel channelName state with
            | Ok (newState, chan) ->
                let ks = user.mat |> Option.map (fun m -> m <| createPartyFlow chan.channelActor userId)
                Ok (newState |> updateUser (addUserChan chan.id ks) userId)
            | Result.Error error -> Result.Error error

    let leave userId chanId state =
        match state.users |> List.tryFind (byUserId userId) with
        | None ->
            Result.Error "User with such id not found"
        | Some user ->
            match user.channels |> Map.tryFind chanId with
            | None ->
                Result.Error "User is not joined channel"
            | Some kso ->
                kso |> Option.map (fun ks -> ks.Shutdown()) |> ignore
                Ok (state |> updateUser (leaveChan chanId) userId)

    let getUserInfo userId state =
        match state.users |> List.tryFind (byUserId userId) with
        | None ->
            Result.Error "User with such id not found"
        | Some user ->
            Ok (getUserInfo user state.channels)

open ServerState
open Helpers

/// Starts IRC server actor.
let startServer (system: ActorSystem) =

    let behavior state (ctx: Actor<ServerControlMessage>) =
        let passError errtext =
            ctx.Sender() <! ServerReplyMessage.Error errtext
            ignored state
        let reply = function
            | Ok result -> ctx.Sender() <! result; ignored state
            | Result.Error errtext -> passError errtext
        let update = function
            | Ok newState -> ignored newState
            | Result.Error errText -> passError errText
        let replyAndUpdate = function
            | Ok (newState, reply) -> ctx.Sender() <! reply; ignored newState
            | Result.Error errText -> passError errText
        let mapReply f = Result.map (fun (ns, r) -> ns, f r)

        function
        | List ->
            ctx.Sender() <!| (state |> ServerApi.listChannels |> Async.map ServerReplyMessage.ChannelList)
            ignored state

        | NewChannel name ->
            state |> ServerApi.addChannel (createChannel system) name
            |> mapReply (getChannelInfo0 >> ServerReplyMessage.ChannelInfo)
            |> replyAndUpdate

        | FindChannel name ->
            state |> ServerApi.findChannel name
            |> Result.map ServerReplyMessage.ChannelInfo
            |> reply

        | SetTopic (chanId, topic) ->
            update (state |> ServerApi.setTopic chanId topic)

        | DropChannel chanId ->
            update (state |> ServerApi.dropChannel chanId)

        | Connect (nick, mat, channels) ->
            state |> ServerApi.connectUser None nick channels mat
            |> mapReply ServerReplyMessage.UserInfo
            |> replyAndUpdate
        
        | Disconnect userId ->
            update (state |> ServerApi.disconnect userId)

        | Join (userId, channelName) ->
            update(state |> ServerApi.join userId channelName (createChannel system))
        
        | Leave (userId, chanId) ->
            update (state |> ServerApi.leave userId chanId)

        | GetUser userId ->
            state |> ServerApi.getUserInfo userId
            |> Result.map ServerReplyMessage.UserInfo
            |> reply
        | ReadState ->
            ctx.Sender() <! state; ignored state
        | UpdateState updater ->
            state |> updater |> ignored

    in
    props <| actorOf2 (behavior { channels = []; users = [] }) |> (spawn system "ircserver")