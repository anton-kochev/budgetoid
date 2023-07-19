module Update exposing (Msg(..), fetchAccountsCommand, update)

import Entities.Account as Account exposing (Account)
import Http
import Json.Decode
import Model exposing (Model)


type Msg
    = SelectAccount String
    | FetchAccounts
      -- | FetchAccountsSuccess (List Account)
      -- | FetchAccountsFailure Http.Error
    | FetchAccountsResponse (Result Http.Error (List Account))


fetchAccountsCommand : String -> Cmd Msg
fetchAccountsCommand userId =
    Http.get
        { url = "http://localhost:7071/api/accounts/" ++ userId

        -- TODO: Try to use the anonymous function instead of the FetchAccountsResponse
        -- , expect = Http.expectJson (\response -> if response == Result.Ok then FetchAccountsSuccess else FetchAccountsFailure) (Json.Decode.list Account.decoder)
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
            ( model, Cmd.none )

        -- FetchAccountsSuccess accounts ->
        --     ( { model | accounts = accounts }, Cmd.none )
        -- FetchAccountsFailure error ->
        --     ( model, Cmd.none )
        SelectAccount accountId ->
            ( { model
                | selectedAccount = List.head (List.filter (\a -> a.id == accountId) model.accounts)
              }
            , Cmd.none
            )
