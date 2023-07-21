port module Ports exposing (..)

-- PORTS


port output : String -> Cmd msg


port messageReceiver : (String -> msg) -> Sub msg


port sendMessage : String -> Cmd msg
