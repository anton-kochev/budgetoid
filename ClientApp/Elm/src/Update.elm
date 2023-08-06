module Update exposing (Msg(..), fetchAccountsCommand, update)

import Browser
import Browser.Navigation as Nav
import Entities.Account as Account exposing (Account)
import Entities.Transaction as Transaction exposing (Transaction)
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
    | FetchTransactions
    | FetchTransactionsResponse (Result Http.Error (List Transaction))
    | UrlChanged Url.Url
    | UrlRequested Browser.UrlRequest
      -- Ports
    | LogMessage String


gotoAccount : String -> Cmd Msg
gotoAccount accountId =
    Nav.pushUrl accountId


logMessage : String -> Cmd Msg
logMessage message =
    Ports.output message


fetchAccountsCommand : String -> Cmd Msg
fetchAccountsCommand userId =
    Http.get
        { url = "http://localhost:7071/api/accounts/" ++ userId
        , expect = Http.expectJson FetchAccountsResponse (Json.Decode.list Account.decoder)
        }


fetchTransactionsCommand : String -> Cmd Msg
fetchTransactionsCommand accountId =
    Http.get
        { url = "http://localhost:7071/api/account/" ++ accountId ++ "/transactions/"
        , expect = Http.expectJson FetchTransactionsResponse (Json.Decode.list Transaction.decoder)
        }


update : Msg -> Model -> ( Model, Cmd Msg )
update msg model =
    case msg of
        -- Not using this yet
        FetchAccounts ->
            ( model, fetchAccountsCommand model.userId )

        FetchAccountsResponse (Ok accounts) ->
            ( { model | accounts = Just accounts }, Cmd.none )

        FetchAccountsResponse (Err error) ->
            ( model, logMessage (Utilities.errorToString error) )

        -- Not using this yet
        FetchTransactions ->
            ( model
            , case model.selectedAccount of
                Just account ->
                    fetchTransactionsCommand account.id

                Nothing ->
                    Cmd.none
            )

        FetchTransactionsResponse (Ok transactions) ->
            ( { model | transactions = Just transactions }, Cmd.none )

        --Just transactions }, Cmd.none )
        FetchTransactionsResponse (Err error) ->
            ( model, logMessage (Utilities.errorToString error) )

        LogMessage message ->
            ( model, logMessage message )

        SelectAccount accountId ->
            gotoAccount accountId

        -- ( { model
        --     | selectedAccount =
        --         List.head
        --             (Maybe.withDefault []
        --                 model.accounts
        --                 |> List.filter (\a -> a.id == accountId)
        --             )
        --   }
        -- , fetchTransactionsCommand accountId
        -- )
        UrlChanged url ->
            ( { model | location = url }
            , Cmd.none
            )

        UrlRequested urlRequest ->
            case urlRequest of
                Browser.Internal url ->
                    --Nav.pushUrl model.key (Url.toString url) )
                    ( model, logMessage ("The new URL is " ++ Url.toString url) )

                Browser.External href ->
                    ( model, Nav.load href )
