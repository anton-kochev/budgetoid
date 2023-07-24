module Account.View exposing (accountsView)

import Entities.Account exposing (Account)
import Html exposing (Html, li, span, text, ul)
import Html.Events exposing (onClick)
import Update exposing (Msg(..))


accountsView : List Account -> Html Msg
accountsView accounts =
    ul [] (List.map accountView accounts)


accountView : Account -> Html Msg
accountView account =
    li [ onClick (SelectAccount account.id) ]
        [ span []
            [ text account.name ]
        , span []
            [ text (":" ++ String.fromFloat account.balance ++ " " ++ account.currency) ]
        ]
