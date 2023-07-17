module Model exposing (Account, Model, initialModel)


type alias Account =
    { id : String
    , name : String
    , balance : Float
    }


type alias Model =
    { -- Define the fields of your application's state
      -- and their initial values
      userId : String
    , accounts : List Account
    }


initialModel : Model
initialModel =
    { -- Initialize the fields of the model with appropriate values
      userId = "00000000-0000-0000-0000-000000000001"
    , accounts =
        [ { id = "00000000-0000-0000-0000-000000000001", name = "Checking", balance = 1000.0 }
        , { id = "00000000-0000-0000-0000-000000000002", name = "Savings", balance = 2000.0 }
        ]
    }
