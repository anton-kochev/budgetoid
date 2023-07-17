module View exposing (view)

import Debug exposing (toString)
import Html exposing (Html, div, text)
import Model exposing (Model)


view : Model -> Html msg
view model =
    div []
        [ div [] [ text "User ID: ", text model.userId ]
        , div [] [ text "Accounts: ", accountsView model.accounts ]
        ]


accountsView : List Model.Account -> Html msg
accountsView accounts =
    div []
        (List.map accountView accounts)


accountView : Model.Account -> Html msg
accountView account =
    div []
        [ div [] [ text ("Account ID: " ++ account.id) ]
        , div [] [ text ("Account Name: " ++ account.name) ]
        , div [] [ text ("Account Balance: " ++ toString account.balance) ]
        ]
