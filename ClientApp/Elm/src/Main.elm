module Main exposing (main)

import Browser
import Html exposing (Html)
import Model exposing (Model, initialModel)
import Update exposing (Msg(..), update)
import Url exposing (Url)
import View



-- Initialize the Elm application


main : Program () Model Msg
main =
    Browser.element
        { init = initApp
        , subscriptions = subscriptionsApp
        , update = update
        , view = view
        }


subscriptionsApp : Model -> Sub Msg
subscriptionsApp model =
    Sub.none



-- Initialize the model


initApp : flags -> ( Model, Cmd Msg )
initApp _ =
    ( initialModel, Update.fetchAccountsCommand initialModel.userId )



-- View function to render the HTML


view : Model -> Html Msg
view model =
    View.view model



-- Update function to handle messages


update : Msg -> Model -> ( Model, Cmd Msg )
update msg model =
    Update.update msg model
