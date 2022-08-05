from flask import Blueprint

api = Blueprint('api', __name__)

from . import transactions # this line should be at the bottom
