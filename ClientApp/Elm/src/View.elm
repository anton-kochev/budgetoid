module View exposing (view)

import Account.View exposing (accountsView)
import Html exposing (Html, div, li, section, text, ul)
import Maybe exposing (withDefault)
import Model exposing (Model)
import Update exposing (Msg(..))


view : Model -> Html Msg
view model =
    div []
        [ div [] [ text "User ID: ", text model.userId ]
        , ul []
            [ li [] [ text "Budget" ]
            , li [] [ text "Report" ]
            , li [] [ text "All accounts" ]
            ]
        , section [] [ text "Accounts", accountsView model.accounts ]
        , div [] [ text "Selected Account: ", text (withDefault "empty" (Maybe.map .id model.selectedAccount)) ]
        ]
