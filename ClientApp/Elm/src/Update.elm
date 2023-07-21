module Update exposing (Msg(..), fetchAccountsCommand, update)

import Browser
import Browser.Navigation as Nav
import Entities.Account as Account exposing (Account)
import Http
import Json.Decode
import Model exposing (Model)
import Ports
import Url
import Utilities


type Msg
    = SelectAccount String
    | FetchAccounts
    | FetchAccountsResponse (Result Http.Error (List Account))
    | UrlChanged Url.Url
    | UrlRequested Browser.UrlRequest
      -- Ports
    | LogMessage String
    | Send
    | Receive String


logMessage : String -> Cmd Msg
logMessage message =
    Ports.output message


fetchAccountsCommand : String -> Cmd Msg
fetchAccountsCommand userId =
    Http.get
        { url = "http://localhost:7071/api/debug/accounts/" ++ userId
        , expect = Http.expectJson FetchAccountsResponse (Json.Decode.list Account.decoder)
        }


update : Msg -> Model -> ( Model, Cmd Msg )
update msg model =
    case msg of
        FetchAccounts ->
            ( model, fetchAccountsCommand model.userId )

        FetchAccountsResponse (Ok accounts) ->
            ( { model | accounts = accounts }, Cmd.none )

        FetchAccountsResponse (Err error) ->
            ( model, logMessage (Utilities.errorToString error) )

        LogMessage message ->
            ( model, logMessage message )

        SelectAccount accountId ->
            ( { model
                | selectedAccount = List.head (List.filter (\a -> a.id == accountId) model.accounts)
              }
            , Cmd.none
            )

        UrlChanged url ->
            ( model
            , logMessage (Url.toString url)
            )

        UrlRequested urlRequest ->
            case urlRequest of
                Browser.Internal url ->
                    --Nav.pushUrl model.key (Url.toString url) )
                    ( model, logMessage ("The new URL is " ++ Url.toString url) )

                Browser.External href ->
                    ( model, Nav.load href )

        Send ->
            ( model, Ports.sendMessage "Ping" )

        Receive message ->
            ( model, logMessage message )
