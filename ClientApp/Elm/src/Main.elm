module Main exposing (main)

import Browser
import Browser.Navigation as Nav
import Model exposing (Model, initialModel)
import Ports
import Update exposing (Msg(..), update)
import Url
import View



-- Initialize the Elm application


main : Program () Model Msg
main =
    Browser.application
        { init = initApp
        , subscriptions = subscriptionsApp
        , update = update
        , view = view
        , onUrlRequest = UrlRequested
        , onUrlChange = UrlChanged
        }


subscriptionsApp : Model -> Sub Msg
subscriptionsApp _ =
    Ports.messageReceiver Receive



-- Initialize the model


initApp : () -> Url.Url -> Nav.Key -> ( Model, Cmd Msg )
initApp _ _ _ =
    ( initialModel, Update.fetchAccountsCommand initialModel.userId )



-- View function to render the HTML


view : Model -> Browser.Document Msg
view model =
    { title = "Budgetoid"
    , body = [ View.view model ]
    }



-- Update function to handle messages


update : Msg -> Model -> ( Model, Cmd Msg )
update msg model =
    Update.update msg model
