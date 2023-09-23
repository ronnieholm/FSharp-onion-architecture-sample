// Source: https://gist.github.com/TheAngryByrd/d88870f8aed6e5dfcbd5912a2c458421

#r "nuget: Serilog, 2.9.0"

open System.Threading.Tasks

[<Interface>]
type ILogger =
    abstract Debug: string -> unit
    abstract Error: string -> unit

[<Interface>]
type ILog =
    abstract Logger: string -> ILogger

let createLogger (name: string) =
    let logger = Serilog.Log.Logger.ForContext("SourceContext", name)

    { new ILogger with
        member _.Debug msg = logger.Debug(msg)
        member _.Error msg = logger.Error(msg) }

[<Interface>]
type IDatabase =
    abstract Query: string * obj -> Task<'o>
    abstract Execute: string * obj -> Task

[<Interface>]
type IDb =
    abstract Database: IDatabase

type Toilet = { Id: int; Name: string; Flushes: int }

[<Interface>]
type IToiletRepository =
    abstract Get: int -> Task<Toilet>
    abstract GetAll: unit -> Task<Toilet list>
    abstract Add: Toilet -> Task
    abstract Update: Toilet -> Task
    abstract Delete: int -> Task

[<Interface>]
type IToiletRepo =
    abstract ToiletRepository: IToiletRepository

let createToiletRepository (connection: IDatabase) =
    { new IToiletRepository with
        member _.Get id =
            connection.Query("SELECT * FROM Toilet WHERE Id = @Id", {| Id = id |})

        member _.GetAll() =
            connection.Query("SELECT * FROM Toilet", null)

        member _.Add toilet =
            connection.Execute("INSERT INTO Toilet (Name, Flushes) VALUES (@Name, @Flushes)", toilet)

        member _.Update toilet =
            connection.Execute("UPDATE Toilet SET Name = @Name, Flushes = @Flushes WHERE Id = @Id", toilet)

        member _.Delete id =
            connection.Execute("DELETE FROM Toilet WHERE Id = @Id", {| Id = id |}) }

type HttpRequest =
    { Method: string
      Path: string
      Query: string
      Body: string
      Headers: Map<string, string> }

type HttpContext = { Request: HttpRequest }

let lookupDatabaseForCustomer _ : IDatabase =
    { new IDatabase with
        member _.Query(query, input) = Task.FromResult Unchecked.defaultof<_>
        member _.Execute(query, input) = Task.CompletedTask }

type AppEnv(ctx: HttpContext) =

    // Scoped
    let database =
        lazy (ctx.Request.Headers.["CustomerId"] |> lookupDatabaseForCustomer)

    let toiletRepository = lazy (createToiletRepository database.Value)

    // Factory
    interface ILog with
        member this.Logger name : ILogger = createLogger name

    interface IToiletRepo with
        member this.ToiletRepository: IToiletRepository = toiletRepository.Value

    interface IDb with
        member _.Database = database.Value


type HttpFuncResult = Task<HttpContext option>
type HttpFunc = HttpContext -> HttpFuncResult
type HttpHandler = HttpFunc -> HttpContext -> HttpFuncResult

let json (data: 'a) (next: HttpFunc) (ctx: HttpContext) =
    // ...
    next ctx

let getToilet (ctx: HttpContext) (next: HttpFunc) (env: AppEnv) =
    // let env = AppEnv(ctx)
    let repo = (env :> IToiletRepo).ToiletRepository

    task {
        let! toilet = repo.Get(ctx.Request.Query |> int)
        return! json toilet next ctx
    }

let withEnv (handler: HttpContext -> (HttpContext -> Task<'a>) -> AppEnv -> Task<'a>) =
    fun (ctx: HttpContext) (next) ->
        task {
            let env = AppEnv(ctx)
            return! handler ctx next env
        }

let app = withEnv getToilet
