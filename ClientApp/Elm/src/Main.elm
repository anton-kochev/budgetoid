module Main exposing (main)

import Browser
import Html exposing (Html)
import Model exposing (Model, initialModel)
import Update exposing (Msg(..), update)
import View



-- Initialize the Elm application


main : Program () Model Msg
main =
    Browser.sandbox { init = init, update = update, view = view }



-- Initialize the model


init : Model
init =
    initialModel



-- View function to render the HTML


view : Model -> Html Msg
view model =
    View.view model



-- Update function to handle messages


update : Msg -> Model -> Model
update msg model =
    case msg of
        -- Handle different messages and update the model accordingly
        NoOp ->
            model
