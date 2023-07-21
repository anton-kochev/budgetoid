module Account.View exposing (accountsView)

import Entities.Account exposing (Account)
import Html exposing (Html, div, li, span, text, ul)
import Html.Events exposing (onClick)
import Update exposing (Msg(..))


accountsView : List Account -> Html Msg
accountsView accounts =
    div [] (List.map accountView accounts)


accountView : Account -> Html Msg
accountView account =
    ul []
        [ li [ onClick (SelectAccount account.id) ]
            [ span []
                [ text account.name ]
            , span []
                [ text (":" ++ String.fromFloat account.balance ++ " " ++ account.currency) ]
            ]
        ]
