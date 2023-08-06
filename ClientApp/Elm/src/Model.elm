module Model exposing (..)

import Entities.Account exposing (Account)
import Entities.Transaction exposing (Transaction)


type alias Model =
    { -- Define the fields of your application's state
      -- and their initial values
      userId : String
    , accounts : Maybe (List Account)
    , transactions : Maybe (List Transaction)
    , selectedAccount : Maybe Account
    , location : String
    }


initialModel : Model
initialModel =
    { accounts = Nothing
    , selectedAccount = Nothing
    , transactions = Nothing
    , userId = "00000000-0000-0000-0000-000000000002"
    , location = "/"
    }
