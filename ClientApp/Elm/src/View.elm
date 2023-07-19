module View exposing (view)

import Debug exposing (toString)
import Entities.Account exposing (Account)
import Html exposing (Html, button, div, span, text)
import Html.Events exposing (onClick)
import Model exposing (Model)
import Update exposing (Msg(..))


view : Model -> Html Msg
view model =
    div []
        [ div [] [ text "User ID: ", text model.userId ]
        , div [] [ text "Accounts: ", accountsView model.accounts ]
        , div [] [ text "Selected Account: ", text (toString model.selectedAccount) ]
        ]


accountsView : List Account -> Html Msg
accountsView accounts =
    div [] (List.map accountView accounts)


accountView : Account -> Html Msg
accountView account =
    div
        [ onClick (SelectAccount account.id) ]
        [ span []
            [ text account.name ]
        , span []
            [ text (":" ++ toString account.balance ++ " " ++ account.currency) ]
        ]
